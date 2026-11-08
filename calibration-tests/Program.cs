using System;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--check-ui-resources")
            {
                UiTests.CheckResources();
                Console.WriteLine("GUI manifest resources, main window icon and About image load successfully.");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--render-ui")
            {
                UiTests.Run(args[1]);
                Console.WriteLine("Rendered initial, live and results calibration views using example measurements.");
                return 0;
            }
            AnalysisTests.Run();
            FirstMovementTests.Run();
            OptimizationFeedbackTests.Run();
            ValorantAimTests.Run();
            ValorantScoringTests.Run();
            CalibrationSearchTests.Run();
            ActiveLabelTests.Run();
            UiTests.Run();
            WorkflowTests.Run();
            Console.WriteLine("Calibration capture, analysis, comparison and native-model checks passed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
