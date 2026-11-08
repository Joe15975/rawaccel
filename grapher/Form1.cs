using System;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using grapher.Models;
using System.IO;
using grapher.Models.Serialized;
using grapher.Models.Theming;
using grapher.Common;

namespace grapher
{
    public partial class RawAcceleration : Form
    {

        #region Constructor


        public RawAcceleration()
        {
            InitializeComponent();

            Version driverVersion = FirstRunSetup.EnsureDriver();

            ToolStripMenuItem HelpMenuItem = new ToolStripMenuItem("&Help");

            HelpMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                    new ToolStripMenuItem("&About", null, (s, e) => {
                        using (var form = new AboutBox(driverVersion))
                        {
                            Theme.Apply(form);

                            form.ShowDialog();
                        }
                    })
            });
            
            var schemes = ColorSchemeManager.LoadSchemes().ToList();
            var themeMenuItem = new ToolStripMenuItem("&Themes");

            themeMenuItem.DropDownItemClicked += (s, e) =>
            {
                if (e.ClickedItem == null) return;

                foreach (ToolStripMenuItem item in themeMenuItem.DropDownItems)
                {
                    item.Checked = e.ClickedItem.Text == item.Text;
                }
            };

            var settings = GUISettings.MaybeLoad();

            Theme.CurrentScheme = ColorSchemeManager.GetSelected(settings, schemes);
            
            foreach (var colorScheme in schemes)
            {
                var menuItem = new ToolStripMenuItem(colorScheme.Name);

                menuItem.Checked = settings?.CurrentColorScheme == colorScheme.Name;
                themeMenuItem.DropDownItems.Add(menuItem);
            }

            menuStrip1.Items.AddRange(new ToolStripItem[] { themeMenuItem, BackgroundServiceMenu.Create(this), HelpMenuItem });

            Theme.Apply(this, menuStrip1);

            AccelGUI = AccelGUIFactory.Construct(
                this,
                AccelerationChart,
                AccelerationChartY,
                VelocityChart,
                VelocityChartY,
                GainChart,
                GainChartY,
                chartContainer,
                accelTypeDropX,
                accelTypeDropY,
                XLutApplyDropdown,
                YLutApplyDropdown,
                CapTypeDropdownXClassic,
                CapTypeDropdownYClassic,
                CapTypeDropdownXPower,
                CapTypeDropdownYPower,
                writeButton,
                toggleButton,
                showVelocityGainToolStripMenuItem,
                showLastMouseMoveToolStripMenuItem,
                AutoWriteMenuItem,
                DeviceMenuItem,
                ScaleMenuItem,
                themeMenuItem,
                DPITextBox,
                PollRateTextBox,
                DirectionalityPanel,
                sensitivityBoxX,
                VertHorzRatioBox,
                rotationBox,
                angleSnappingBox,
                inCapBoxXClassic,
                inCapBoxYClassic,
                outCapBoxXClassic,
                outCapBoxYClassic,
                inCapBoxXPower,
                inCapBoxYPower,
                outCapBoxXPower,
                outCapBoxYPower,
                inputJumpBoxX,
                inputJumpBoxY,
                outputJumpBoxX,
                outputJumpBoxY,
                inputOffsetBoxX,
                inputOffsetBoxY,
                outputOffsetBoxX,
                outputOffsetBoxY,
                accelerationBoxX,
                accelerationBoxY,
                decayRateBoxX,
                decayRateBoxY,
                gammaBoxX,
                gammaBoxY,
                smoothBoxX,
                smoothBoxY,
                scaleBoxX,
                scaleBoxY,
                limitBoxX,
                limitBoxY,
                powerBoxX,
                powerBoxY,
                expBoxX,
                expBoxY,
                syncSpeedBoxX,
                syncSpeedBoxY,
                DomainBoxX,
                DomainBoxY,
                RangeBoxX,
                RangeBoxY,
                LpNormBox,
                sensXYLock,
                ByComponentXYLock,
                FakeBox,
                WholeCheckBox,
                ByComponentCheckBox,
                gainSwitchX,
                gainSwitchY,
                XLutActiveValuesBox,
                YLutActiveValuesBox,
                XLutPointsBox,
                YLutPointsBox,
                LockXYLabel,
                sensitivityLabel,
                VertHorzRatioLabel,
                rotationLabel,
                angleSnappingLabel,
                inCapLabelXClassic,
                inCapLabelYClassic,
                outCapLabelXClassic,
                outCapLabelYClassic,
                CapTypeLabelXClassic,
                CapTypeLabelYClassic,
                inCapLabelXPower,
                inCapLabelYPower,
                outCapLabelXPower,
                outCapLabelYPower,
                CapTypeLabelXPower,
                CapTypeLabelYPower,
                inputJumpLabelX,
                inputJumpLabelY,
                outputJumpLabelX,
                outputJumpLabelY,
                inputOffsetLabelX,
                inputOffsetLabelY,
                outputOffsetLabelX,
                outputOffsetLabelY,
                constantOneLabelX,
                constantOneLabelY,
                decayRateLabelX,
                decayRateLabelY,
                gammaLabelX,
                gammaLabelY,
                smoothLabelX,
                smoothLabelY,
                scaleLabelX,
                scaleLabelY,
                limitLabelX,
                limitLabelY,
                powerLabelX,
                powerLabelY,
                expLabelX,
                expLabelY,
                LUTTextLabelX,
                LUTTextLabelY,
                constantThreeLabelX,
                constantThreeLabelY,
                ActiveValueTitle,
                ActiveValueTitleY,
                SensitivityMultiplierActiveLabel,
                VertHorzRatioActiveLabel,
                RotationActiveLabel,
                AngleSnappingActiveLabel,
                InCapActiveXLabelClassic,
                InCapActiveYLabelClassic,
                OutCapActiveXLabelClassic,
                OutCapActiveYLabelClassic,
                CapTypeActiveXLabelClassic,
                CapTypeActiveYLabelClassic,
                InCapActiveXLabelPower,
                InCapActiveYLabelPower,
                OutCapActiveXLabelPower,
                OutCapActiveYLabelPower,
                CapTypeActiveXLabelPower,
                CapTypeActiveYLabelPower,
                InputJumpActiveXLabel,
                InputJumpActiveYLabel,
                OutputJumpActiveXLabel,
                OutputJumpActiveYLabel,
                InputOffsetActiveXLabel,
                InputOffsetActiveYLabel,
                OutputOffsetActiveXLabel,
                OutputOffsetActiveYLabel,
                AccelerationActiveLabelX,
                AccelerationActiveLabelY,
                DecayRateActiveXLabel,
                DecayRateActiveYLabel,
                GammaActiveXLabel,
                GammaActiveYLabel,
                SmoothActiveXLabel,
                SmoothActiveYLabel,
                ScaleActiveXLabel,
                ScaleActiveYLabel,
                LimitActiveXLabel,
                LimitActiveYLabel,
                PowerClassicActiveXLabel,
                PowerClassicActiveYLabel,
                ExpActiveXLabel,
                ExpActiveYLabel,
                SyncSpeedActiveXLabel,
                SyncSpeedActiveYLabel,
                AccelTypeActiveLabelX,
                AccelTypeActiveLabelY,
                gainSwitchActiveLabelX,
                gainSwitchActiveLabelY,
                OptionSetXTitle,
                OptionSetYTitle,
                MouseLabel,
                DirectionalityLabel,
                DirectionalityX,
                DirectionalityY,
                DirectionalityActiveValueTitle,
                LPNormLabel,
                LpNormActiveValue,
                DirectionalDomainLabel,
                DomainActiveValueX,
                DomainActiveValueY,
                DirectionalityRangeLabel,
                RangeActiveValueX,
                RangeActiveValueY,
                XLutApplyLabel,
                YLutApplyLabel,
                LutApplyActiveXLabel,
                LutApplyActiveYLabel);

            var profilesMenu = new ToolStripMenuItem("&Profiles");
            profilesMenu.DropDownOpening += (s, e) => PopulateProfilesMenu(profilesMenu);
            var editProfile = new ToolStripMenuItem("Edit acceleration profile");
            editProfile.DropDownOpening += (s, e) =>
            {
                editProfile.DropDownItems.Clear();
                for (int i = 0; i < AccelGUI.Settings.ActiveConfig.profiles.Count; i++)
                {
                    int index = i;
                    var item = new ToolStripMenuItem(AccelGUI.Settings.ActiveConfig.profiles[i].name)
                    { Checked = i == AccelGUI.Settings.EditingProfileIndex };
                    item.Click += (sender, args) =>
                    {
                        if (index == AccelGUI.Settings.EditingProfileIndex) return;
                        if (MessageBox.Show(this, "Switch the profile being edited? Unapplied field edits will be discarded.",
                            "Edit acceleration profile", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                        AccelGUI.Settings.SelectProfileForEditing(index);
                        AccelGUI.RefreshActive();
                    };
                    editProfile.DropDownItems.Add(item);
                }
            };
            advancedToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] {
                new ToolStripSeparator(),
                new ToolStripMenuItem("&Calibrate", null, (s, e) => OpenCalibration()),
                profilesMenu,
                editProfile
            });

        }

        private static string ProfilesDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RawAccel", "Profiles");

        private void OpenCalibration()
        {
            try
            {
                AccelGUI.Settings.RefreshConnectedDevices();
                using (var form = new CalibrationForm(AccelGUI.Settings, AccelGUI.MouseWatcher))
                {
                    Theme.Apply(form);
                    AccelGUI.MouseWatcher.PauseCharts = true;
                    try { form.ShowDialog(this); }
                    finally { AccelGUI.MouseWatcher.PauseCharts = false; }
                    if (form.OutcomeUncertain)
                        throw new InvalidOperationException("Calibration recovery could not be verified. Check Background > Service status and the active driver settings before applying another configuration.");
                    if (form.SettingsAccepted)
                    {
                        if (RawAccel.ServiceSupport.SettingsFingerprint.Compute(DriverConfig.GetActive().ToJSON()) !=
                            RawAccel.ServiceSupport.SettingsFingerprint.Compute(form.AcceptedSettingsJson))
                            throw new InvalidOperationException("Active settings changed after calibration. Reopen Raw Accel to reload them.");
                        AccelGUI.Settings.ReloadAfterCalibration(form.AcceptedSettingsJson, true);
                        AccelGUI.Settings.SelectProfileForEditing(form.SelectedProfileIndex);
                        AccelGUI.RefreshUser();
                        AccelGUI.RefreshActive();
                    }
                    else if (form.RecoveryRestored)
                    {
                        string current = DriverConfig.GetActive().ToJSON();
                        if (RawAccel.ServiceSupport.SettingsFingerprint.Compute(current) !=
                            RawAccel.ServiceSupport.SettingsFingerprint.Compute(form.VerifiedRecoverySettingsJson))
                            throw new InvalidOperationException("Active settings changed after recovery. Reopen Raw Accel to reload them.");
                        AccelGUI.Settings.ReloadAfterCalibration(current, false);
                        AccelGUI.RefreshActivePreservingEdits();
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Calibration could not finish", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void PopulateProfilesMenu(ToolStripMenuItem menu)
        {
            menu.DropDownItems.Clear();
            menu.DropDownItems.Add("Save applied settings as...", null, (s, e) => SaveProfile());
            menu.DropDownItems.Add(new ToolStripSeparator());
            try
            {
                Directory.CreateDirectory(ProfilesDirectory);
                var paths = Directory.GetFiles(ProfilesDirectory, "*.json").OrderBy(Path.GetFileName).ToArray();
                if (paths.Length == 0)
                    menu.DropDownItems.Add(new ToolStripMenuItem("No saved profiles") { Enabled = false });
                var deleteMenu = new ToolStripMenuItem("Delete profile");
                foreach (var path in paths)
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    menu.DropDownItems.Add(name, null, (s, e) => ActivateProfile(path));
                    deleteMenu.DropDownItems.Add(name, null, (s, e) => DeleteProfile(path));
                }
                if (paths.Length > 0)
                {
                    menu.DropDownItems.Add(new ToolStripSeparator());
                    menu.DropDownItems.Add(deleteMenu);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Unable to list profiles");
            }
        }

        private void SaveProfile()
        {
            try
            {
                Directory.CreateDirectory(ProfilesDirectory);
                using (var dialog = new SaveFileDialog { InitialDirectory = ProfilesDirectory, Filter = "JSON files (*.json)|*.json", DefaultExt = "json", AddExtension = true, Title = "Save applied settings as profile" })
                {
                    dialog.FileOk += (s, e) =>
                    {
                        if (!string.Equals(Path.GetDirectoryName(dialog.FileName), ProfilesDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            MessageBox.Show(this, "Save profiles in the Profiles folder so they appear in the menu.", "Save profile");
                            e.Cancel = true;
                        }
                    };
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        File.WriteAllText(dialog.FileName, AccelGUI.Settings.ActiveConfig.ToJSON());
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to save profile"); }
        }

        private void ActivateProfile(string path)
        {
            if (MessageBox.Show(this, "Apply this profile? Any unapplied edits will be discarded.", "Switch profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try
            {
                var (config, error) = DriverConfig.Convert(File.ReadAllText(path));
                if (error != null || !AccelGUI.Settings.TryActivate(config, out error))
                {
                    MessageBox.Show(this, error, "Unable to apply profile");
                    return;
                }
                AccelGUI.RefreshUser();
                AccelGUI.RefreshActive();
                AccelGUI.Settings.SetActiveHandles();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to apply profile"); }
        }

        private void DeleteProfile(string path)
        {
            if (MessageBox.Show(this, $"Delete profile '{Path.GetFileNameWithoutExtension(path)}'?", "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try { File.Delete(path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Unable to delete profile"); }
        }

        #endregion Constructor

        #region Properties

        public AccelGUI AccelGUI { get; }

        #endregion Properties

        #region Methods

        protected override void WndProc(ref Message m)
        {
            if (!(AccelGUI is null))
            {
                if (m.Msg == 0x00ff) // WM_INPUT
                {
                    AccelGUI.MouseWatcher.ReadMouseMove(m);
                }
                else if (m.Msg == 0x00fe) // WM_INPUT_DEVICE_CHANGE
                {
                    AccelGUI.Settings.OnDeviceChangeMessage();
                }
            }

            base.WndProc(ref m);
        }

        public void ResetAutoScroll()
        {
            chartsPanel.AutoScrollPosition = Constants.Origin;
        }

        public void ResizeAndCenter()
        {
            ResetAutoScroll();

            var workingArea = Screen.FromControl(this).WorkingArea;
            var chartsPreferredSize = chartsPanel.GetPreferredSize(Constants.MaxSize);

            Size = new Size
            {
                Width = Math.Min(workingArea.Width, optionsPanel.Size.Width + chartsPreferredSize.Width),
                Height = Math.Min(workingArea.Height, chartsPreferredSize.Height + 48)
            };

            Location = new Point
            {
                X = workingArea.X + (workingArea.Width - Size.Width) / 2,
                Y = workingArea.Y + (workingArea.Height - Size.Height) / 2
            };

        }

        #endregion Method

        static void MakeStartupShortcut(bool gui)
        {
            var startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);

            if (string.IsNullOrEmpty(startupFolder))
            {
                throw new Exception("Startup folder does not exist");
            }

            //Windows Script Host Shell Object
            Type t = Type.GetTypeFromCLSID(new Guid("72C24DD5-D70A-438B-8A42-98424B88AFB8"));
            dynamic shell = Activator.CreateInstance(t);

            try
            {
                // Delete any other RA related startup shortcuts
                var candidates = new[] { "rawaccel", "raw accel", "writer" };

                foreach (string path in Directory.EnumerateFiles(startupFolder, "*.lnk")
                    .Where(f => candidates.Any(f.Substring(startupFolder.Length).ToLower().Contains)))
                {
                    var link = shell.CreateShortcut(path);
                    try
                    {
                        string targetPath = link.TargetPath;

                        if (!(targetPath is null) && 
                            (targetPath.EndsWith("rawaccel.exe") ||
                                targetPath.EndsWith("writer.exe") &&
                                    new FileInfo(targetPath).Directory.GetFiles("rawaccel.exe").Any()))
                        {
                            File.Delete(path);
                        }
                    }
                    finally
                    {
                        Marshal.FinalReleaseComObject(link);
                    }
                }

                var name = gui ? "rawaccel" : "writer";

                var lnk = shell.CreateShortcut($@"{startupFolder}\{name}.lnk");

                try
                {
                    if (!gui) lnk.Arguments = "\"" + Constants.DefaultSettingsFileName + "\"";
                    lnk.TargetPath = $@"{Application.StartupPath}\{name}.exe";
                    lnk.WorkingDirectory = Application.StartupPath;
                    lnk.Save();
                }
                finally
                {
                    Marshal.FinalReleaseComObject(lnk);
                }

            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private void RawAcceleration_FormClosing(object sender, FormClosingEventArgs e)
        {
            Properties.Settings.Default.Size = Size;
            Properties.Settings.Default.Location = Location;
            Properties.Settings.Default.Save();
        }

        private void RawAcceleration_Shown(object sender, EventArgs e)
        {
            FirstRunSetup.EnsureServiceAsync(this);
            var sizeDirect = chartsPanel.GetPreferredSize(Constants.MaxSize);
            Console.WriteLine($"[BeginInvoke] Direct size: {sizeDirect}");

            if (Properties.Settings.Default.HasRunBefore)
            {
                var savedSettingsRect = new Rectangle(Properties.Settings.Default.Location, Properties.Settings.Default.Size);
                bool isSavedSettingsVisible = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(savedSettingsRect));
                if (isSavedSettingsVisible)
                {
                    //this.Location = Properties.Settings.Default.Location;
                    //this.Size = Properties.Settings.Default.Size;
                }
                else
                {
                    ResizeAndCenter();
                }
            }
            else
            {
                Properties.Settings.Default.HasRunBefore = true;
                Properties.Settings.Default.Save();
                IAsyncResult result = this.BeginInvoke(new MethodInvoker(() =>
                {
                    ResizeAndCenter();
                }));
                this.EndInvoke(result);
            }
        }
    }
}
