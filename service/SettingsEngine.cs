using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using RawAccel.ServiceSupport;

namespace RawAccel.Service
{
    public interface IAccelerationDriver
    {
        string Normalize(string json);
        string ReadActive();
        void Apply(string json);
    }

    // All writes (boot, GUI and resume) share this lock. No mouse input runs through the service.
    public sealed class SettingsEngine
    {
        private readonly object gate = new object();
        private readonly IAccelerationDriver driver;
        private readonly string path;
        private readonly string journalBase;
        private readonly Func<DateTime> utcNow;
        private readonly Func<long> monotonicNow;
        private bool userApplied;
        private TrialJournal trial;
        private TrialJournal completed;
        private Timer trialTimer;
        private long monotonicDeadline, monotonicLimit;
        private readonly Dictionary<string, Tuple<string, ServiceResponse>> responses = new Dictionary<string, Tuple<string, ServiceResponse>>();

        private sealed class TrialJournal
        {
            public string LeaseId, Owner, Baseline, Previous, Next, SavedFingerprint, Phase, CommitRequestId, CommitRequestContent;
            public string RecoverRequestId, RecoverRequestContent, RecoveryExpectedFingerprint;
            public bool RecoverySavedChanged;
            public DateTime ExpiresUtc, LimitUtc;
        }
        private string JournalPath { get { return journalBase + ".trial"; } }
        private string ReceiptPath { get { return journalBase + ".trial.receipt"; } }
        private static string Fingerprint(string json) { return SettingsFingerprint.Compute(json); }
        private string Active() { return driver.Normalize(driver.ReadActive()); }
        private string SavedFingerprint()
        {
            return File.Exists(path) ? Fingerprint(ReadValidated(path)) : "missing";
        }
        private string NormalizeCandidate(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > ServiceProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Invalid candidate size.");
            Fingerprint(json); // Reject non-finite input before conversion.
            string result = driver.Normalize(json);
            Fingerprint(result);
            if (Encoding.UTF8.GetByteCount(result) > ServiceProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Candidate is too large.");
            return result;
        }
        private void WriteJournal()
        {
            WriteDurable(JournalPath, trial);
        }
        private void WriteDurable(string destination, TrialJournal value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string temp = destination + ".pending";
            byte[] data = new UTF8Encoding(false).GetBytes(JsonConvert.SerializeObject(value));
            using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { f.Write(data, 0, data.Length); f.Flush(true); }
            if (File.Exists(destination)) File.Replace(temp, destination, null);
            else File.Move(temp, destination);
        }
        private void LoadReceipt()
        {
            if (completed != null || !File.Exists(ReceiptPath)) return;
            var receipt = JsonConvert.DeserializeObject<TrialJournal>(File.ReadAllText(ReceiptPath));
            if (receipt == null || (receipt.Phase != "commit" && receipt.Phase != "recovered") || receipt.Owner == null || receipt.LeaseId == null ||
                receipt.Baseline == null || receipt.Next == null ||
                (receipt.Phase == "commit" && (receipt.CommitRequestId == null || receipt.CommitRequestContent == null)) ||
                (receipt.Phase == "recovered" && (receipt.RecoverRequestId == null || receipt.RecoverRequestContent == null)))
                throw new InvalidDataException("Invalid calibration commit receipt.");
            if (utcNow() >= receipt.LimitUtc) { File.Delete(ReceiptPath); return; }
            completed = receipt;
        }
        private void ClearTrial()
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
            trial = null;
            if (trialTimer != null) { trialTimer.Dispose(); trialTimer = null; }
        }
        private void CheckSaved()
        {
            if (SavedFingerprint() != trial.SavedFingerprint)
                throw new IOException("Startup settings changed during calibration; recovery requires attention.");
        }
        private void EnsureExpected(string actual, string expected)
        {
            if (Fingerprint(actual) != Fingerprint(expected))
                throw new IOException("Active settings changed outside calibration; recovery requires attention.");
        }
        private ServiceResponse TrialResponse(string state, string active)
        {
            return new ServiceResponse { Success = true, SettingsApplied = hasAppliedSettings, TrialProtocolVersion = 1,
                Message = status, TrialState = state, LeaseId = trial == null ? null : trial.LeaseId,
                ActiveSettings = active, BaselineSettings = trial == null ? null : trial.Baseline,
                LeaseExpiresUtc = trial == null ? null : trial.ExpiresUtc.ToString("o") };
        }
        private void EnsureResponseFits(string baseline, string candidate)
        {
            var response = new ServiceResponse { TrialProtocolVersion = 1, ActiveSettings = candidate,
                BaselineSettings = baseline, Message = new string('x', 512), LeaseId = new string('x', 64),
                TrialState = "active", LeaseExpiresUtc = DateTime.UtcNow.ToString("o") };
            if (Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(response)) > ServiceProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Settings are too large for a trial response.");
        }
        private void Remember(string key, string request, ServiceResponse response)
        {
            if (responses.Count >= 256) responses.Clear();
            responses[key] = Tuple.Create(request, response);
        }
        private void ArmTimer()
        {
            if (trialTimer != null) trialTimer.Dispose();
            var delay = trial.ExpiresUtc - utcNow();
            double monotonicSeconds = (monotonicDeadline - monotonicNow()) / (double)Stopwatch.Frequency;
            if (monotonicSeconds < delay.TotalSeconds) delay = TimeSpan.FromSeconds(Math.Max(0, monotonicSeconds));
            if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
            trialTimer = new Timer(_ => ProcessTrialTimer(), null, delay, Timeout.InfiniteTimeSpan);
        }
        private bool DeadlineReached()
        {
            return utcNow() >= trial.ExpiresUtc || monotonicNow() >= monotonicDeadline;
        }
        internal void ProcessTrialTimer()
        {
            lock (gate)
            {
                if (trial == null) return;
                try
                {
                    if (DeadlineReached()) RecoverTrial();
                    else ArmTimer(); // A clock adjustment or an early callback must not lose the deadline.
                }
                catch (Exception e)
                {
                    status = "Calibration recovery failed; retrying: " + e.Message;
                    hasAppliedSettings = false;
                    if (trial != null && trialTimer != null) trialTimer.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
                }
            }
        }
        // Recovery recognizes both sides of an interrupted transition, never a third-party value.
        private void RecoverTrial()
        {
            if (trial == null)
            {
                if (!File.Exists(JournalPath)) return;
                trial = LoadJournal();
            }
            string live = Active();
            string liveHash = Fingerprint(live);
            string saved = SavedFingerprint();
            if (trial.Phase == "recover")
                throw new IOException("An explicit recovery was interrupted; retry it with the original request.");
            if (trial.Phase == "commit" && saved == Fingerprint(trial.Next))
            {
                EnsureExpected(live, trial.Next);
                if (trial.CommitRequestId != null && trial.CommitRequestContent != null)
                { WriteDurable(ReceiptPath, trial); completed = trial; }
                status = "Accepted calibration recovered after interruption.";
                hasAppliedSettings = true; userApplied = true; ClearTrial(); return;
            }
            if (saved != trial.SavedFingerprint)
                throw new IOException("Startup settings changed during calibration; recovery requires attention.");
            if (liveHash != Fingerprint(trial.Previous) && liveHash != Fingerprint(trial.Next) && liveHash != Fingerprint(trial.Baseline))
                throw new IOException("Active settings changed outside calibration; recovery requires attention.");
            if (liveHash != Fingerprint(trial.Baseline))
            {
                driver.Apply(trial.Baseline);
                EnsureExpected(Active(), trial.Baseline);
            }
            status = "Calibration cancelled; previous live settings restored. Startup settings were not changed.";
            hasAppliedSettings = true; userApplied = true; ClearTrial();
        }

        private TrialJournal LoadJournal()
        {
            if (new FileInfo(JournalPath).Length > 4L * ServiceProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Calibration recovery journal is too large.");
            var value = JsonConvert.DeserializeObject<TrialJournal>(File.ReadAllText(JournalPath));
            if (value == null || string.IsNullOrWhiteSpace(value.Owner) || string.IsNullOrWhiteSpace(value.LeaseId) ||
                value.Baseline == null || value.Previous == null || value.Next == null || value.SavedFingerprint == null ||
                (value.Phase != "preview" && value.Phase != "commit" && value.Phase != "recover"))
                throw new InvalidDataException("Invalid calibration recovery journal.");
            NormalizeCandidate(value.Baseline); NormalizeCandidate(value.Previous); NormalizeCandidate(value.Next);
            return value;
        }

        private ServiceResponse RecoveryCommand(ServiceRequest request, string owner)
        {
            if (trial == null && File.Exists(JournalPath)) trial = LoadJournal();
            string live = Active();
            if (trial == null)
            {
                if (request.Command == "trial-recover")
                {
                    LoadReceipt();
                    if (completed == null || completed.Phase != "recovered" || completed.Owner != owner || completed.LeaseId != request.LeaseId)
                        throw new InvalidOperationException("No calibration recovery is pending.");
                    if (completed.RecoverRequestId != request.RequestId || completed.RecoverRequestContent != JsonConvert.SerializeObject(request))
                        throw new InvalidDataException("Recovery request does not match the completed transaction.");
                    EnsureExpected(live, completed.Baseline);
                    var receipt = TrialResponse("recovered", live);
                    receipt.LeaseId = completed.LeaseId; receipt.BaselineSettings = completed.Baseline;
                    receipt.RecoverySavedChanged = completed.RecoverySavedChanged;
                    return receipt;
                }
                return TrialResponse("idle", live);
            }
            if (trial.Owner != owner) throw new UnauthorizedAccessException("Calibration recovery belongs to another account.");
            bool savedChanged;
            try { savedChanged = SavedFingerprint() != trial.SavedFingerprint; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is UnauthorizedAccessException)
            { savedChanged = true; }
            if (request.Command == "trial-recovery-query")
            {
                var pending = TrialResponse("recovery-pending", live);
                pending.RecoverySavedChanged = savedChanged;
                return pending;
            }
            if (request.LeaseId != trial.LeaseId) throw new UnauthorizedAccessException("Calibration recovery token is invalid.");
            string content = JsonConvert.SerializeObject(request);
            if (trial.Phase == "recover")
            {
                if (trial.RecoverRequestId == request.RequestId && trial.RecoverRequestContent != content)
                    throw new InvalidDataException("Recovery request ID was reused with different content.");
                if (trial.RecoverRequestId != request.RequestId)
                {
                    if (request.ExpectedFingerprint != Fingerprint(live)) throw new IOException("Active settings changed; review recovery again.");
                    trial.RecoverRequestId = request.RequestId; trial.RecoverRequestContent = content;
                    trial.RecoveryExpectedFingerprint = request.ExpectedFingerprint; WriteJournal();
                }
                if (Fingerprint(live) != trial.RecoveryExpectedFingerprint && Fingerprint(live) != Fingerprint(trial.Baseline))
                    throw new IOException("Active settings changed; review recovery again.");
                savedChanged = trial.RecoverySavedChanged;
            }
            else
            {
                if (request.ExpectedFingerprint != Fingerprint(live)) throw new IOException("Active settings changed; review recovery again.");
                trial.Phase = "recover"; trial.RecoverRequestId = request.RequestId; trial.RecoverRequestContent = content;
                trial.RecoveryExpectedFingerprint = request.ExpectedFingerprint; trial.RecoverySavedChanged = savedChanged;
                WriteJournal();
            }
            string baseline = trial.Baseline, lease = trial.LeaseId;
            if (Fingerprint(live) != Fingerprint(baseline)) { driver.Apply(baseline); EnsureExpected(Active(), baseline); }
            var recoveryReceipt = JsonConvert.DeserializeObject<TrialJournal>(JsonConvert.SerializeObject(trial));
            recoveryReceipt.Phase = "recovered"; recoveryReceipt.LimitUtc = utcNow().AddMinutes(10);
            WriteDurable(ReceiptPath, recoveryReceipt); completed = recoveryReceipt;
            ClearTrial();
            userApplied = true; hasAppliedSettings = true;
            status = "Previous live settings restored; startup settings were not changed.";
            var result = TrialResponse("recovered", Active());
            result.LeaseId = lease; result.BaselineSettings = baseline; result.RecoverySavedChanged = savedChanged;
            return result;
        }

        public void EndTrialForShutdown()
        {
            lock (gate) { if (trial != null || File.Exists(JournalPath)) RecoverTrial(); }
        }

        internal void ExpireTrial()
        {
            lock (gate) { if (trial != null && DeadlineReached()) RecoverTrial(); }
        }

        private void FailTrial(Exception error)
        {
            try
            {
                if (File.Exists(JournalPath)) RecoverTrial();
                else ClearTrial();
            }
            catch (Exception recovery)
            {
                status = "Calibration recovery failed: " + recovery.Message; hasAppliedSettings = false;
                throw new IOException("Calibration failed and recovery remains unresolved: " + recovery.Message, error);
            }
            throw new IOException("Calibration failed; recovery was completed: " + error.Message, error);
        }

        public ServiceResponse TrialCommand(ServiceRequest request, string owner)
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128)
                    throw new InvalidDataException("Authenticated caller and request ID are required.");
                if (request.Command == "trial-recovery-query" || request.Command == "trial-recover")
                    return RecoveryCommand(request, owner);
                if (trial == null && File.Exists(JournalPath)) RecoverTrial();
                if (trial != null && DeadlineReached()) RecoverTrial();
                if (completed != null && utcNow() >= completed.LimitUtc) completed = null;
                if (trial == null && completed == null && request.Command != "trial-begin") LoadReceipt();
                string requestKey = owner + "\n" + request.RequestId;
                string requestContent = JsonConvert.SerializeObject(request);
                Tuple<string, ServiceResponse> prior;
                if (responses.TryGetValue(requestKey, out prior))
                {
                    if (prior.Item1 != requestContent) throw new InvalidDataException("Request ID was reused with different content.");
                    if (prior.Item2.TrialState == "active" && (trial == null || prior.Item2.LeaseId != trial.LeaseId))
                        throw new IOException("The original calibration lease is no longer active.");
                    if (prior.Item2.TrialState == "active" && Fingerprint(Active()) != Fingerprint(prior.Item2.ActiveSettings))
                        throw new IOException("The calibration state has changed since the original request.");
                    if (prior.Item2.TrialState == "committed" && (completed == null || prior.Item2.LeaseId != completed.LeaseId))
                        throw new IOException("The original calibration commit is no longer available.");
                    if (prior.Item2.TrialState == "committed")
                    {
                        EnsureExpected(Active(), completed.Next);
                        if (SavedFingerprint() != Fingerprint(completed.Next))
                            throw new IOException("Startup settings changed after calibration.");
                    }
                    return prior.Item2;
                }
                ServiceResponse result;
                if (trial == null && completed != null && completed.Phase == "commit" && utcNow() < completed.LimitUtc &&
                    request.LeaseId == completed.LeaseId && owner == completed.Owner &&
                    (request.Command == "trial-query" || request.Command == "trial-restore" || request.Command == "trial-commit"))
                {
                    string current = Active();
                    if (request.Command == "trial-commit" &&
                        (request.RequestId != completed.CommitRequestId || requestContent != completed.CommitRequestContent))
                        throw new InvalidDataException("The commit request does not match the completed transaction.");
                    if (request.Command == "trial-restore")
                    {
                        if (request.ExpectedFingerprint != Fingerprint(completed.Next) ||
                            Fingerprint(current) != Fingerprint(completed.Next) || SavedFingerprint() != Fingerprint(completed.Next))
                            throw new IOException("Settings changed after calibration; previous settings were not restored.");
                        string baseline = completed.Baseline;
                        ApplyAndSave(baseline);
                        current = Active(); EnsureExpected(current, baseline);
                        result = TrialResponse("restored", current);
                        result.BaselineSettings = baseline; result.LeaseId = completed.LeaseId;
                        if (File.Exists(ReceiptPath)) File.Delete(ReceiptPath);
                        completed = null;
                    }
                    else
                    {
                        EnsureExpected(current, completed.Next);
                        if (SavedFingerprint() != Fingerprint(completed.Next))
                            throw new IOException("Startup settings changed after calibration.");
                        result = TrialResponse("committed", current);
                        result.LeaseId = completed.LeaseId; result.BaselineSettings = completed.Baseline;
                        result.LeaseExpiresUtc = completed.LimitUtc.ToString("o");
                    }
                    Remember(requestKey, requestContent, result);
                    return result;
                }
                if (request.Command == "trial-begin")
                {
                    if (trial != null || File.Exists(JournalPath)) throw new IOException("A calibration trial or recovery is already active.");
                    string baseline = Active();
                    if (Fingerprint(baseline) != request.ExpectedFingerprint) throw new IOException("Active settings changed before calibration.");
                    string candidate = NormalizeCandidate(request.Settings);
                    EnsureResponseFits(baseline, candidate);
                    var now = utcNow();
                    trial = new TrialJournal { LeaseId = Guid.NewGuid().ToString("N"), Owner = owner, Baseline = baseline,
                        Previous = baseline, Next = candidate, SavedFingerprint = SavedFingerprint(), Phase = "preview",
                        ExpiresUtc = now.AddSeconds(30), LimitUtc = now.AddMinutes(10) };
                    long monotonicStart = monotonicNow();
                    monotonicDeadline = monotonicStart + 30 * Stopwatch.Frequency;
                    monotonicLimit = monotonicStart + 600 * Stopwatch.Frequency;
                    string live = null;
                    try
                    {
                        WriteJournal(); ArmTimer();
                        CheckSaved(); EnsureExpected(Active(), baseline);
                        driver.Apply(candidate);
                        live = Active(); EnsureExpected(live, candidate);
                    }
                    catch (Exception e) { FailTrial(e); }
                    status = "Calibration trial active."; userApplied = true;
                    result = TrialResponse("active", live);
                }
                else
                {
                    if (trial == null || request.LeaseId != trial.LeaseId || owner != trial.Owner)
                        throw new UnauthorizedAccessException("Calibration lease is invalid or expired.");
                    if (request.Command == "trial-cancel")
                    {
                        string baseline = trial.Baseline; string lease = trial.LeaseId;
                        RecoverTrial(); result = TrialResponse("cancelled", Active());
                        result.BaselineSettings = baseline; result.LeaseId = lease;
                        Remember(requestKey, requestContent, result); return result;
                    }
                    if (trial.Phase != "preview")
                    {
                        RecoverTrial();
                        throw new IOException("An interrupted calibration commit was reconciled; check service status.");
                    }
                    CheckSaved();
                    string live = Active(); EnsureExpected(live, trial.Next);
                    switch (request.Command)
                    {
                        case "trial-update":
                            if (Fingerprint(live) != request.ExpectedFingerprint) throw new IOException("Calibration candidate changed.");
                            string candidate = NormalizeCandidate(request.Settings);
                            EnsureResponseFits(trial.Baseline, candidate);
                            try
                            {
                                trial.Previous = live; trial.Next = candidate; WriteJournal();
                                CheckSaved(); EnsureExpected(Active(), live);
                                driver.Apply(candidate); live = Active(); EnsureExpected(live, candidate);
                            }
                            catch (Exception e) { FailTrial(e); }
                            result = TrialResponse("active", live); break;
                        case "trial-renew":
                            var oldExpiry = trial.ExpiresUtc;
                            trial.ExpiresUtc = utcNow().AddSeconds(30);
                            if (trial.ExpiresUtc > trial.LimitUtc) trial.ExpiresUtc = trial.LimitUtc;
                            try { WriteJournal(); }
                            catch { trial.ExpiresUtc = oldExpiry; ArmTimer(); throw; }
                            monotonicDeadline = Math.Min(monotonicNow() + 30 * Stopwatch.Frequency, monotonicLimit);
                            ArmTimer(); result = TrialResponse("active", live); break;
                        case "trial-query": result = TrialResponse("active", live); break;
                        case "trial-commit":
                            if (trial.SavedFingerprint == Fingerprint(trial.Next) &&
                                Fingerprint(trial.Baseline) != trial.SavedFingerprint)
                                throw new IOException("Cannot safely distinguish this commit from an interrupted save; restore the original settings first.");
                            var accepted = trial;
                            try { trial.Phase = "commit"; trial.CommitRequestId = request.RequestId;
                                trial.CommitRequestContent = requestContent; WriteJournal(); CommitTrial(live); }
                            catch (Exception e)
                            {
                                // Atomic replacement may have succeeded even when cleanup or acknowledgement failed.
                                if (trial != null && accepted.SavedFingerprint != Fingerprint(accepted.Next) &&
                                    SavedFingerprint() == Fingerprint(accepted.Next)) RecoverTrial();
                                else FailTrial(e);
                            }
                            completed = accepted;
                            result = TrialResponse("committed", Active());
                            result.LeaseId = request.LeaseId; result.BaselineSettings = accepted.Baseline;
                            result.LeaseExpiresUtc = accepted.LimitUtc.ToString("o"); break;
                        default: throw new InvalidDataException("Unknown trial command.");
                    }
                }
                Remember(requestKey, requestContent, result);
                return result;
            }
        }

        private void CommitTrial(string live)
        {
            string staging = path + ".pending";
            try
            {
                using (var f = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                { byte[] bytes = new UTF8Encoding(false).GetBytes(live); f.Write(bytes, 0, bytes.Length); f.Flush(true); }
                CheckSaved(); EnsureExpected(Active(), trial.Next);
                if (File.Exists(path)) File.Replace(staging, path, path + ".lastgood");
                else File.Move(staging, path);
                WriteDurable(ReceiptPath, trial);
                status = "Calibration accepted and saved."; hasAppliedSettings = true; userApplied = true;
                ClearTrial();
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }

        private bool hasAppliedSettings;
        private string status = "Waiting to restore startup settings.";

        public SettingsEngine(IAccelerationDriver driver, string path, Func<DateTime> utcNow = null, Func<long> monotonicNow = null, string journalBase = null)
        {
            this.driver = driver;
            this.path = path;
            this.journalBase = journalBase ?? path;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.monotonicNow = monotonicNow ?? (() => Stopwatch.GetTimestamp());
        }

        public string Status { get { lock (gate) return status; } }
        public bool HasPendingTrial { get { lock (gate) return trial != null || File.Exists(JournalPath); } }
        public void PrepareStandalone()
        {
            lock (gate)
            {
                if (trial != null || File.Exists(JournalPath)) RecoverTrial();
                Active();
                hasAppliedSettings = true;
                status = "Standalone calibration host is ready.";
            }
        }
        public ServiceSupport.ServiceResponse GetStatus()
        {
            lock (gate) return new ServiceSupport.ServiceResponse
            {
                Success = true, SettingsApplied = hasAppliedSettings, Message = status, TrialProtocolVersion = 1
            };
        }

        public void ReportFailure(string message)
        {
            lock (gate) { status = message; hasAppliedSettings = false; }
        }

        public void Restore(bool startup)
        {
            lock (gate)
            {
                // A live lease owns the driver until it expires or is explicitly ended. A
                // queued startup/resume restore must not silently cancel its preview.
                if (trial != null)
                {
                    if (DeadlineReached()) RecoverTrial();
                    return;
                }
                // An orphaned journal belongs to a previous host and must be reconciled first.
                if (File.Exists(JournalPath)) { RecoverTrial(); return; }
                // A GUI Apply while the driver was starting must win over the boot retry.
                if (startup && userApplied) return;
                string json;
                bool backup = false;
                try { json = ReadValidated(path); }
                catch (Exception e) when (e is InvalidDataException || e is IOException)
                {
                    if (!File.Exists(path + ".lastgood")) throw;
                    json = ReadValidated(path + ".lastgood");
                    backup = true;
                }
                driver.Apply(json);
                hasAppliedSettings = true;
                status = backup ? "Restored backup settings; save current settings to repair startup settings."
                    : "Saved settings applied at " + DateTime.Now.ToString("g") + ".";
            }
        }

        public void ApplyAndSave(string json)
        {
            lock (gate)
            {
                if (trial != null || File.Exists(JournalPath)) throw new IOException("Calibration trial or recovery is active.");
                string normalized = driver.Normalize(json);
                if (Encoding.UTF8.GetByteCount(normalized) > ServiceSupport.ServiceProtocol.MaximumMessageBytes)
                    throw new InvalidDataException("Settings are too large.");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string staging = path + ".pending";
                try
                {
                    // Fail on storage errors before changing the live mouse settings.
                    using (var file = new FileStream(staging, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(normalized);
                        file.Write(bytes, 0, bytes.Length);
                        file.Flush(true);
                    }
                    string previous = driver.ReadActive();
                    driver.Apply(normalized);
                    try
                    {
                        if (File.Exists(path)) File.Replace(staging, path, path + ".lastgood");
                        else File.Move(staging, path);
                    }
                    catch (Exception saveError)
                    {
                        try { driver.Apply(previous); }
                        catch (Exception rollbackError)
                        {
                            throw new IOException("Settings could not be saved or rolled back. " +
                                "Live settings may differ from startup settings: " + rollbackError.Message, saveError);
                        }
                        throw new IOException("Settings could not be saved. Previous live settings restored.", saveError);
                    }
                    userApplied = true;
                    hasAppliedSettings = true;
                    status = "Settings applied and saved at " + DateTime.Now.ToString("g") + ".";
                }
                finally
                {
                    if (File.Exists(staging)) File.Delete(staging);
                }
            }
        }

        private string ReadValidated(string file)
        {
            if (new FileInfo(file).Length > ServiceSupport.ServiceProtocol.MaximumMessageBytes)
                throw new InvalidDataException("Startup settings are too large.");
            return driver.Normalize(File.ReadAllText(file));
        }
    }
}
