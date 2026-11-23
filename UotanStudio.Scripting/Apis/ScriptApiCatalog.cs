using Jint;
using UotanStudio.Scripting.Validation;

namespace UotanStudio.Scripting.Apis;

internal static class ScriptApiCatalog
{
    internal static void Register(Engine engine, ScriptApiContext context, ScriptLimits limits)
    {
        var textLimit = Math.Min(2_048, limits.MaxResultLength);
        engine.SetValue("log", new LogApi(context.Log, textLimit));
        engine.SetValue("fastboot", new FastbootApi(context.Fastboot));
        engine.SetValue("image", new ImageApi(context.Image));
        engine.SetValue("ui", new UiApi(context.Ui, limits.MaxUiElements, textLimit));
        engine.SetValue("math", new MathApi());
        engine.SetValue("unit", new UnitApi());
    }

    private sealed class LogApi(ILogSink sink, int maxLength)
    {
        public void info(string message) => sink.Write(ScriptLogLevel.Info, ScriptInputValidator.Text(message, maxLength, "message"));

        public void warn(string message) => sink.Write(ScriptLogLevel.Warn, ScriptInputValidator.Text(message, maxLength, "message"));

        public void error(string message) => sink.Write(ScriptLogLevel.Error, ScriptInputValidator.Text(message, maxLength, "message"));
    }

    private sealed class FastbootApi(IFastbootScriptApi api)
    {
        public string flash(string partition, string imagePath) =>
            api.Flash(ScriptInputValidator.PartitionName(partition), ScriptInputValidator.AbsolutePath(imagePath));

        public string reboot(string mode) => api.Reboot(ScriptInputValidator.RebootMode(mode));

        public string getVar(string name) => api.GetVar(ScriptInputValidator.VariableName(name));

        public string setActiveSlot(string slot) => api.SetActiveSlot(ScriptInputValidator.Slot(slot));
    }

    private sealed class ImageApi(IImageScriptApi api)
    {
        public string parsePayload(string path) => api.ParsePayload(ScriptInputValidator.AbsolutePath(path));

        public string parseSuper(string path) => api.ParseSuper(ScriptInputValidator.AbsolutePath(path));

        public string getPartitionInfo(string path, string partition) =>
            api.GetPartitionInfo(ScriptInputValidator.AbsolutePath(path), ScriptInputValidator.PartitionName(partition));
    }

    private sealed class UiApi(IUiScriptApi api, int maxElements, int maxLength)
    {
        public bool confirm(string message) => api.Confirm(ScriptInputValidator.Text(message, maxLength, "message"));

        public void alert(string message) => api.Alert(ScriptInputValidator.Text(message, maxLength, "message"));

        public void progressStart(string title) => api.ProgressStart(ScriptInputValidator.Text(title, maxLength, "title"));

        public void progressUpdate(int percent)
        {
            if (percent is < 0 or > 100)
            {
                throw new ArgumentException("Progress percent must be between 0 and 100.", nameof(percent));
            }

            api.ProgressUpdate(percent);
        }

        public void progressDone(string message) => api.ProgressDone(ScriptInputValidator.Text(message, maxLength, "message"));

        public string showPanel(string descriptionJson) =>
            api.ShowPanel(ScriptInputValidator.PanelDescription(descriptionJson, maxElements));
    }

    private sealed class MathApi
    {
        public double clamp(double value, double minimum, double maximum)
        {
            if (!double.IsFinite(value) || !double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
            {
                throw new ArgumentException("Clamp arguments must be finite and ordered.");
            }

            return Math.Clamp(value, minimum, maximum);
        }

        public double roundTo(double value, int digits)
        {
            if (!double.IsFinite(value) || digits is < 0 or > 15)
            {
                throw new ArgumentException("Round arguments are outside the accepted range.");
            }

            return Math.Round(value, digits, MidpointRounding.AwayFromZero);
        }
    }

    private sealed class UnitApi
    {
        public double convert(double value, string from, string to) =>
            ScriptInputValidator.ConvertUnit(value, from, to);
    }
}