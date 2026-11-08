using grapher.Common;
using grapher.Models.Options.Directionality;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace grapher.Models.Options
{
    public class ApplyOptions
    {
        #region Constructors

        public ApplyOptions(
            CheckBox byComponentVectorXYLock,
            AccelOptionSet optionSetX,
            AccelOptionSet optionSetY,
            DirectionalityOptions directionalityOptions,
            Option sensitivity,
            LockableOption yxRatio,
            Option rotation,
            Option angleSnapping,
            Label lockXYLabel,
            AccelCharts accelCharts)
        {
            Directionality = directionalityOptions;
            WholeVectorCheckBox = Directionality.WholeCheckBox;
            ByComponentVectorCheckBox = Directionality.ByComponentCheckBox;

            WholeVectorCheckBox.Click += new System.EventHandler(OnWholeClicked);
            ByComponentVectorCheckBox.Click += new System.EventHandler(OnByComponentClicked);

            WholeVectorCheckBox.CheckedChanged += new System.EventHandler(OnWholeCheckedChange);
            ByComponentVectorCheckBox.CheckedChanged += new System.EventHandler(OnByComponentCheckedChange);

            ByComponentVectorXYLock = byComponentVectorXYLock;
            OptionSetX = optionSetX;
            OptionSetY = optionSetY;
            Sensitivity = sensitivity;
            YToXRatio = yxRatio;
            Rotation = rotation;
            AngleSnapping = angleSnapping;
            LockXYLabel = lockXYLabel;
            AccelCharts = accelCharts;

            LockXYLabel.AutoSize = false;
            LockXYLabel.TextAlign = ContentAlignment.MiddleCenter;

            ByComponentVectorXYLock.CheckedChanged += new System.EventHandler(OnByComponentXYLockChecked);
            ByComponentVectorXYLock.Checked = true;

            YToXRatio.SnapTo(Sensitivity);
            Rotation.SnapTo(YToXRatio);
            AngleSnapping.SnapTo(Rotation);

            EnableWholeApplication();
        }

        #endregion Constructors

        #region Properties

        public CheckBox WholeVectorCheckBox { get; }

        public CheckBox ByComponentVectorCheckBox { get; }

        public CheckBox ByComponentVectorXYLock { get; }

        public AccelOptionSet OptionSetX { get; }

        public AccelOptionSet OptionSetY { get; }

        public DirectionalityOptions Directionality { get; }

        public Option Sensitivity { get; }

        public LockableOption YToXRatio { get; }

        public Option Rotation { get; }

        public Option AngleSnapping { get; }

        public AccelCharts AccelCharts { get; }

        public Label ActiveValueTitleY { get; }

        public Label LockXYLabel { get; }

        public bool IsWhole { get; private set; }

        #endregion Properties

        #region Methods

        public void SetArgsFromActiveValues(ref AccelArgs argsX, ref AccelArgs argsY)
        {
            OptionSetX.SetArgsFromActiveValues(ref argsX);

            if (ByComponentVectorXYLock.Checked)
            {
                OptionSetX.SetArgsFromActiveValues(ref argsY);
            }
            else
            {
                OptionSetY.SetArgsFromActiveValues(ref argsY);
            }
        }

        public void SetActiveValues(Profile settings)
        {
            Sensitivity.SetActiveValue(Helper.GetSensitivityFactor(settings));
            YToXRatio.SetActiveValue(settings.yxOutputDPIRatio);
            Rotation.SetActiveValue(settings.rotation);
            AngleSnapping.SetActiveValue(settings.snap);
            
            WholeVectorCheckBox.Checked = settings.inputSpeedArgs.combineMagnitudes;
            ByComponentVectorCheckBox.Checked = !settings.inputSpeedArgs.combineMagnitudes;
            ByComponentVectorXYLock.Checked = settings.argsX.IsEquivalentTo(settings.argsY);
            OptionSetX.SetActiveValues(ref settings.argsX);
            OptionSetY.SetActiveValues(ref settings.argsY);

            Directionality.SetActiveValues(settings);

            AccelCharts.SetLogarithmic(
                OptionSetX.Options.AccelerationType.LogarithmicCharts,
                OptionSetY.Options.AccelerationType.LogarithmicCharts);
        }

        public void UpdateActiveLabels(Profile settings)
        {
            Sensitivity.UpdateActiveValue(Helper.GetSensitivityFactor(settings));
            YToXRatio.Option.UpdateActiveValue(settings.yxOutputDPIRatio);
            Rotation.UpdateActiveValue(settings.rotation);
            AngleSnapping.UpdateActiveValue(settings.snap);
            OptionSetX.Options.UpdateActiveLabels(ref settings.argsX);
            OptionSetY.Options.UpdateActiveLabels(ref settings.argsY);
            Directionality.Domain.ActiveValueLabels.SetValues(settings.domainXY.x, settings.domainXY.y);
            Directionality.Range.ActiveValueLabels.SetValues(settings.rangeXY.x, settings.rangeXY.y);
            if (settings.inputSpeedArgs.combineMagnitudes)
                Directionality.LpNorm.UpdateActiveValue(settings.inputSpeedArgs.lpNorm);
            else
                Directionality.LpNorm.ActiveValueLabel.SetValue("—");
            AccelCharts.SetLogarithmic(
                OptionSetX.Options.ActiveUsesLogarithmicCharts(ref settings.argsX),
                OptionSetY.Options.ActiveUsesLogarithmicCharts(ref settings.argsY));
        }

        public void OnWholeClicked(object sender, EventArgs e)
        {
            if (!WholeVectorCheckBox.Checked)
            {
                WholeVectorCheckBox.Checked = true;
                ByComponentVectorCheckBox.Checked = false;
                Directionality.ToWhole();
            }
        }

        public void OnByComponentClicked(object sender, EventArgs e)
        {
            if (!ByComponentVectorCheckBox.Checked)
            {
                WholeVectorCheckBox.Checked = false;
                ByComponentVectorCheckBox.Checked = true;
                Directionality.ToByComponent();
            }
        }

        public void OnWholeCheckedChange(object sender, EventArgs e)
        {
            if (WholeVectorCheckBox.Checked)
            {
                EnableWholeApplication();
            }
        }

        public void OnByComponentCheckedChange(object sender, EventArgs e)
        {
            if (ByComponentVectorCheckBox.Checked)
            {
                EnableByComponentApplication();
            }
        }

        public void ShowWholeSet()
        {
            OptionSetX.SetRegularMode();
            OptionSetY.Hide();
            //SetActiveTitlesWhole();
        }

        public void ShowByComponentAsOneSet()
        {
            OptionSetX.SetTitleMode("X = Y");
            OptionSetY.Hide();
            //SetActiveTitlesByComponents();
        }

        public void ShowByComponentAsTwoSets()
        {
            OptionSetX.SetTitleMode("X");
            OptionSetY.SetTitleMode("Y");
            OptionSetY.Show();
        }

        public void ShowByComponentSet()
        {
            if (ByComponentVectorXYLock.Checked)
            {
                ShowByComponentAsOneSet();
            }
            else
            {
                ShowByComponentAsTwoSets();
            }
        }

        private void OnByComponentXYLockChecked(object sender, EventArgs e)
        {
            if (!IsWhole)
            {
                ShowByComponentSet();
            }
        }

        public void EnableWholeApplication()
        {
            IsWhole = true;
            ByComponentVectorXYLock.Hide();
            ShowWholeSet();
        }

        public void EnableByComponentApplication()
        {
            IsWhole = false;
            ByComponentVectorXYLock.Show();
            ShowByComponentSet();
        }

        private void SetActiveTitlesWhole()
        {
            OptionSetX.ActiveValuesTitle.Left = OptionSetX.Options.Left + OptionSetX.Options.Width;
            LockXYLabel.Width = (AccelCharts.Left - OptionSetX.ActiveValuesTitle.Left) / 2;
            OptionSetX.ActiveValuesTitle.Width = LockXYLabel.Width;
            LockXYLabel.Left = OptionSetX.ActiveValuesTitle.Left + OptionSetX.ActiveValuesTitle.Width;
            YToXRatio.LockBox.Left = LockXYLabel.Left + LockXYLabel.Width / 2 - YToXRatio.LockBox.Width / 2;
            ByComponentVectorXYLock.Left = YToXRatio.LockBox.Left;
            AlignActiveValues();
        }

        private void SetActiveTitlesByComponents()
        {
            OptionSetY.ActiveValuesTitle.Left = OptionSetY.Options.Left + OptionSetY.Options.Width;
            OptionSetY.ActiveValuesTitle.Width = Constants.NarrowChartLeft - OptionSetY.ActiveValuesTitle.Left;
            AlignActiveValues();
        }

        private void AlignActiveValues()
        {
            OptionSetX.AlignActiveValues();
            OptionSetY.AlignActiveValues();
            Sensitivity.AlignActiveValues();
            Rotation.AlignActiveValues();
            AngleSnapping.AlignActiveValues();
        }

        #endregion Methods
    }
}
