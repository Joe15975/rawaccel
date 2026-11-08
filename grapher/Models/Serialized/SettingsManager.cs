using Newtonsoft.Json;
using System;
using System.IO;
using System.Windows.Forms;
using System.Threading;
using System.Text;
using System.Drawing;
using grapher.Models.Devices;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using grapher.Models.Theming;
using grapher.Common;
using RawAccel.ServiceSupport;

namespace grapher.Models.Serialized
{
    public class SettingsManager
    {
        #region Constructors

        public SettingsManager(
            Field dpiField,
            Field pollRateField,
            ToolStripMenuItem autoWrite,
            ToolStripMenuItem showLastMouseMove,
            ToolStripMenuItem showVelocityAndGain,
            ToolStripMenuItem themeMenu,
            Form form
            )
        {
            DpiField = dpiField;
            PollRateField = pollRateField;
            AutoWriteMenuItem = autoWrite;
            ShowLastMouseMoveMenuItem = showLastMouseMove;
            ShowVelocityAndGainMoveMenuItem = showVelocityAndGain;
            ThemeMenu = themeMenu;
            Form = form;

            SystemDevices = new List<MultiHandleDevice>();
            ActiveNormTaggedHandles = new List<(IntPtr, bool)>();

            GuiSettings = GUISettings.MaybeLoad();

            SelectedTheme = GetSelectedTheme();
            AddEventsToThemeMenu();

            if (GuiSettings is null)
            {
                GuiSettings = MakeGUISettingsFromFields();
                GuiSettings.Save();
            }
            else
            {
                UpdateFieldsFromGUISettings();
            }

            UserConfigField = InitActiveAndGetUserConfig();
            RefreshConnectedDevices();
        }

        #endregion Constructors

        #region Fields

        private EventHandler DeviceChangeField;

        private DriverConfig ActiveConfigField;
        private DriverConfig UserConfigField;

        private ManagedAccel ActiveAccelForGraphingField;
        private int editingProfileIndex;

        #endregion Fields

        #region Properties

        public GUISettings GuiSettings { get; private set; }

        public event EventHandler DeviceChange
        {
            add => DeviceChangeField += value;
            remove => DeviceChangeField -= value;
        }

        public DriverConfig ActiveConfig 
        {
            get => ActiveConfigField;

            private set
            {
                if (ActiveConfigField != value)
                {
                    ActiveConfigField = value;
                    ActiveProfileNamesSet = new HashSet<string>(value.profiles.Select(p => p.name));
                }

                editingProfileIndex = Math.Min(editingProfileIndex, value.profiles.Count - 1);
                ActiveAccelForGraphingField = value.accels[editingProfileIndex].CreateStatelessCopy();
            }
        }

        public Profile ActiveProfile 
        {
            get => ActiveConfigField.profiles[editingProfileIndex];
            private set => ActiveConfigField.SetProfileAt(editingProfileIndex, value);
        }

        public ManagedAccel ActiveAccel
        {
            get => ActiveConfig.accels[editingProfileIndex];
        }

        /// <summary>
        /// Version of active accel modified to be appropriate for graphing (stateless)
        /// </summary>
        public ManagedAccel ActiveAccelForGraphing
        {
            get => ActiveAccelForGraphingField;
        }

        public DriverConfig UserConfig 
        { 
            get => UserConfigField;
            private set => UserConfigField = value;
        }

        public Profile UserProfile 
        {
            get => UserConfigField.profiles[editingProfileIndex];
            private set => UserConfigField.SetProfileAt(editingProfileIndex, value);
        }

        public HashSet<string> ActiveProfileNamesSet { get; private set; }
        public int EditingProfileIndex { get { return editingProfileIndex; } }

        public void SelectProfileForEditing(int index)
        {
            if (index < 0 || index >= ActiveConfig.profiles.Count || index >= UserConfig.profiles.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            editingProfileIndex = index;
            ActiveAccelForGraphingField = ActiveConfig.accels[index].CreateStatelessCopy();
            SetActiveHandles();
        }

        public Field DpiField { get; private set; }

        public Field PollRateField { get; private set; }

        public IList<MultiHandleDevice> SystemDevices { get; private set; }

        public List<(IntPtr, bool)> ActiveNormTaggedHandles { get; }

        private ToolStripMenuItem AutoWriteMenuItem { get; set; }

        private ToolStripMenuItem ShowLastMouseMoveMenuItem { get; set; }

        private ToolStripMenuItem ShowVelocityAndGainMoveMenuItem { get; set; }
        private ToolStripMenuItem ThemeMenu { get; set; }
        private string SelectedTheme { get; set; }
        private Form Form { get; set; }

        #endregion Properties

        #region Methods

        public void ResetDriver()
        {
            if (ServiceClient.IsEnabled)
            {
                if (!TryActivate(DriverConfig.GetDefault(), out string error))
                    throw new ApplicationException(error);
            }
            else
            {
                DriverConfig.Deactivate();
                ActiveConfig = DriverConfig.GetDefault();
            }
        }

        public void UpdateFieldsFromGUISettings()
        {
            DpiField.SetToEntered(GuiSettings.DPI);
            PollRateField.SetToEntered(GuiSettings.PollRate);
            ShowLastMouseMoveMenuItem.Checked = GuiSettings.ShowLastMouseMove;
            ShowVelocityAndGainMoveMenuItem.Checked = GuiSettings.ShowVelocityAndGain;
            AutoWriteMenuItem.Checked = GuiSettings.AutoWriteToDriverOnStartup;
        }

        public bool TryActivate(Profile settings, out string errors)
        {
            var old = UserProfile;
            UserProfile = settings;
            bool success = TryActivate(UserConfig, out errors);
            if (!success)
            {
                UserProfile = old;
            }
            return success;
        }

        public bool TryActivate(DriverConfig settings, out string errors)
        {
            errors = settings.Errors();

            if (errors == null)
            {
                try
                {
                    if (ServiceClient.IsEnabled)
                    {
                        var response = ServiceClient.Send("apply", settings.ToJSON());
                        if (!response.Success) { errors = response.Message; return false; }
                    }
                    else
                    {
                        settings.Activate();
                        File.WriteAllText(Constants.DefaultSettingsFileName, settings.ToJSON());
                    }
                }
                catch (Exception e)
                {
                    errors = e.Message;
                    return false;
                }

                GuiSettings = MakeGUISettingsFromFields();
                GuiSettings.Save();

                UserConfig = settings;
                ActiveConfig = settings;
            }

            return errors == null;
        }

        // Calibration already applied/committed through the service. Reload without writing again.
        public void ReloadAfterCalibration(string verifiedJson, bool accepted)
        {
            var active = DriverConfig.Convert(verifiedJson);
            if (active.Item2 != null) throw new InvalidDataException(active.Item2);
            ActiveConfig = active.Item1;
            if (accepted)
            {
                var user = DriverConfig.Convert(verifiedJson);
                if (user.Item2 != null) throw new InvalidDataException(user.Item2);
                UserConfig = user.Item1;
            }
            SetActiveHandles();
        }

        public void SetHiddenOptions(Profile settings)
        {
            settings.maximumSpeed = UserProfile.maximumSpeed;
            settings.minimumSpeed = UserProfile.minimumSpeed;
            settings.lrOutputDPIRatio = UserProfile.lrOutputDPIRatio;
            settings.udOutputDPIRatio = UserProfile.udOutputDPIRatio;
            settings.inputSpeedArgs.inputSmoothHalflife = UserProfile.inputSpeedArgs.inputSmoothHalflife;
            settings.inputSpeedArgs.scaleSmoothHalflife = UserProfile.inputSpeedArgs.scaleSmoothHalflife;
            settings.inputSpeedArgs.outputSmoothHalflife = UserProfile.inputSpeedArgs.outputSmoothHalflife;
        }

        public GUISettings MakeGUISettingsFromFields()
        {
            return new GUISettings
            {
                DPI = (int)DpiField.Data,
                PollRate = (int)PollRateField.Data,
                ShowLastMouseMove = ShowLastMouseMoveMenuItem.Checked,
                ShowVelocityAndGain = ShowVelocityAndGainMoveMenuItem.Checked,
                AutoWriteToDriverOnStartup = AutoWriteMenuItem.Checked,
                CurrentColorScheme = SelectedTheme
            };
        }

        public void SetActiveHandles()
        {
            ActiveNormTaggedHandles.Clear();

            bool ActiveProfileIsFirst = ActiveProfile == ActiveConfig.profiles[0];

            foreach (var sysDev in SystemDevices)
            {
                void AddHandlesFromSysDev(bool normalized)
                {
                    ActiveNormTaggedHandles.AddRange(sysDev.handles.Select(h => (h, normalized)));
                }

                var settings = ActiveConfig.devices.Find(d => d.id == sysDev.id);

                if (settings is null)
                {
                    if (ActiveProfileIsFirst && !ActiveConfig.defaultDeviceConfig.disable)
                    {
                        AddHandlesFromSysDev(ActiveConfig.defaultDeviceConfig.dpi > 0);
                    }
                }
                else if (!settings.config.disable &&
                            ((ActiveProfileIsFirst &&
                                    (string.IsNullOrEmpty(settings.profile) ||
                                        !ActiveProfileNamesSet.Contains(settings.profile))) ||
                                ActiveProfile.name == settings.profile))
                {
                    AddHandlesFromSysDev(settings.config.dpi > 0);
                }
            }
        }

        public void Submit(DeviceConfig newDefaultConfig, DeviceDialogItem[] items)
        {
            UserConfig.defaultDeviceConfig = newDefaultConfig;
            foreach (var item in items)
            {
                if (item.overrideDefaultConfig)
                {
                    if (item.oldSettings is null)
                    {
                        UserConfig.devices.Add(
                            new DeviceSettings
                            {
                                name = item.device.name,
                                profile = item.newProfile,
                                id = item.device.id,
                                config = item.newConfig
                            });
                    }
                    else
                    {
                        item.oldSettings.config = item.newConfig;
                        item.oldSettings.profile = item.newProfile;
                    }
                }
                else if (!(item.oldSettings is null))
                {
                    UserConfig.devices.Remove(item.oldSettings);
                }
            }
        }

        public void OnProfileSelectionChange()
        {
            SetActiveHandles();
        }

        public void OnDeviceChangeMessage()
        {
            RefreshConnectedDevices();

            DeviceChangeField?.Invoke(this, EventArgs.Empty);
        }

        public void RefreshConnectedDevices()
        {
            SystemDevices = MultiHandleDevice.GetList();
            SetActiveHandles();
        }

        private DriverConfig InitActiveAndGetUserConfig()
        {
            // The service restores the machine-wide configuration. Opening the GUI must not
            // overwrite it with a stale portable settings file or duplicate a boot-time write.
            if (ServiceClient.IsEnabled)
            {
                ActiveConfig = DriverConfig.GetActive();
                if (File.Exists(ServiceProtocol.SettingsPath))
                {
                    try
                    {
                        var saved = DriverConfig.Convert(File.ReadAllText(ServiceProtocol.SettingsPath));
                        if (saved.Item2 == null) return saved.Item1;
                    }
                    catch (JsonException) { }
                }
                return ActiveConfig;
            }
            var path = Constants.DefaultSettingsFileName;
            if (File.Exists(path))
            {
                try
                {
                    var (cfg, err) = DriverConfig.Convert(File.ReadAllText(path));

                    if (err == null)
                    {
                        if (GuiSettings.AutoWriteToDriverOnStartup)
                        {
                            if (!TryActivate(cfg, out string _))
                            {
                                throw new Exception("deserialization succeeded but TryActivate failed");
                            }
                        }
                        else
                        {
                            ActiveConfig = DriverConfig.GetActive();
                        }

                        return cfg;
                    }
                }
                catch (JsonException e)
                {
                    System.Diagnostics.Debug.WriteLine($"bad settings: {e}");
                }
            }

            ActiveConfig = DriverConfig.GetActive();
            File.WriteAllText(path, ActiveConfig.ToJSON());
            return ActiveConfig;
        }

        private string GetSelectedTheme()
        {
            if (GuiSettings == null || string.IsNullOrEmpty(GuiSettings.CurrentColorScheme))
            {
                return "Light Theme";
            }

            return GuiSettings.CurrentColorScheme;
        }

        private void AddEventsToThemeMenu()
        {
            foreach (ToolStripMenuItem item in ThemeMenu.DropDownItems)
            {
                item.Click += (s, e) =>
                {
                    if (GuiSettings.CurrentColorScheme == item.Name)
                    {
                        return;
                    }

                    SelectedTheme = item.Text;
                    var colorScheme = ColorSchemeManager.FromName(item.Text);

                    Theme.CurrentScheme = colorScheme;
                    Theme.Apply(Form);
                    GuiSettings.CurrentColorScheme = colorScheme.Name;
                    GuiSettings.Save();

                    MessageBox.Show(@"If you switched between a light and dark theme, some colors might be off. 
If this is the case, please restart the app to apply all colors correctly."); 
                };
            }
        }

        #endregion Methods
    }
}
