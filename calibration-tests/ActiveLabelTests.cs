using System;
using System.Windows.Forms;
using grapher;
using grapher.Models.Options;
using grapher.Models.Options.LUT;
using grapher.Models.Theming;

public static class ActiveLabelTests
{
    public static void Run()
    {
        Theme.CurrentScheme = ColorScheme.LightTheme;
        using (var form = new Form())
        using (var box = new TextBox())
        using (var label = new Label())
        using (var active = new Label())
        using (var center = new Label())
        using (var points = new RichTextBox())
        using (var activePoints = new RichTextBox())
        {
            var field = new Field(box, form, 1);
            var option = new Option(field, label, new ActiveValueLabel(active, center), 0);
            field.SetToEntered(3.25);
            string text = box.Text;
            var state = field.State;
            option.UpdateActiveValue(2);
            if (box.Text != text || field.Data != 3.25 || field.State != state || string.IsNullOrEmpty(active.Text))
                throw new Exception("Active label refresh changed a pending numeric edit.");
            var lut = new LUTPanelOptions(points, activePoints);
            points.Text = "pending lookup data";
            lut.SetActiveValues(new float[] { 1, 2, 3, 4 }, 4, AccelMode.lut, false);
            if (points.Text != "pending lookup data" || string.IsNullOrEmpty(activePoints.Text))
                throw new Exception("Active lookup refresh changed a pending lookup edit.");
            points.Text = string.Empty;
            lut.SetActiveValues(new float[] { 1, 2, 3, 4 }, 4, AccelMode.lut, false);
            if (points.Text != string.Empty) throw new Exception("Active lookup refresh populated an empty edit.");
        }
    }
}
