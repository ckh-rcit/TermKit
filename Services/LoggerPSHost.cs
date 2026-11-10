using System;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Text;

namespace TermKit.Services
{
    /// <summary>
    /// Minimal PSHost implementation that forwards all host output (Write-Host, progress, prompts)
    /// into the application's Logger instead of a real console.
    /// Ported from Entra Scout's LoggerPSHost.
    /// </summary>
    public class LoggerPSHost : PSHost
    {
        private readonly LoggerPSHostUserInterface _ui = new();
        private readonly Guid _instanceId = Guid.NewGuid();

        public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
        public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
        public override Guid InstanceId => _instanceId;
        public override string Name => "TermKitHost";
        public override PSHostUserInterface UI => _ui;
        public override Version Version => new(1, 0, 0);

        public override void EnterNestedPrompt() { }
        public override void ExitNestedPrompt() { }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) { }
    }

    public class LoggerPSHostUserInterface : PSHostUserInterface
    {
        private readonly LoggerPSHostRawUserInterface _rawUi = new();
        private readonly StringBuilder _lineBuffer = new();

        public override PSHostRawUserInterface RawUI => _rawUi;

        public override string ReadLine() => string.Empty;

        public override System.Security.SecureString ReadLineAsSecureString() => new();

        public override void Write(string value) => _lineBuffer.Append(value);

        public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Write(value);

        public override void WriteLine(string value)
        {
            FlushLineBuffer();
            Logger.Log(value);
        }

        public override void WriteLine()
        {
            FlushLineBuffer();
        }

        public override void WriteErrorLine(string value)
        {
            FlushLineBuffer();
            Logger.Log($"ERROR: {value}", LogType.Error);
        }

        public override void WriteDebugLine(string message) => Logger.Log($"DEBUG: {message}");

        public override void WriteProgress(long sourceId, System.Management.Automation.ProgressRecord record) { }

        public override void WriteVerboseLine(string message) => Logger.Log($"VERBOSE: {message}");

        public override void WriteWarningLine(string message)
        {
            // The AD module warns on import when the PC has no default domain (non-joined); every call passes -Server, so it is noise.
            if (message.StartsWith("Error initializing default drive", StringComparison.OrdinalIgnoreCase)) return;
            Logger.Log($"WARNING: {message}", LogType.Warning);
        }

        // Note: warnings are still logged as LogType.Warning here so they're captured in Logger's
        // history; the UI (MainWindow) is responsible for hiding LogType.Warning lines by default
        // and revealing them when the user checks "Show Warnings".

        public override System.Collections.Generic.Dictionary<string, System.Management.Automation.PSObject> Prompt(
            string caption, string message, System.Collections.ObjectModel.Collection<System.Management.Automation.Host.FieldDescription> descriptions)
        {
            Logger.Log($"{caption}: {message}");
            return new System.Collections.Generic.Dictionary<string, System.Management.Automation.PSObject>();
        }

        public override int PromptForChoice(string caption, string message, System.Collections.ObjectModel.Collection<ChoiceDescription> choices, int defaultChoice)
        {
            Logger.Log($"{caption}: {message}");
            throw new InvalidOperationException($"PowerShell requires an explicit choice: {caption}: {message}. Review the publisher in an interactive PowerShell session before retrying.");
        }

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
            PromptForCredential(caption, message, userName, targetName, PSCredentialTypes.Default, PSCredentialUIOptions.Default);

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName,
            PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        {
            Logger.Log($"{caption}: {message}");
            return new PSCredential(userName ?? string.Empty, new System.Security.SecureString());
        }

        private void FlushLineBuffer()
        {
            if (_lineBuffer.Length == 0)
            {
                return;
            }

            Logger.Log(_lineBuffer.ToString());
            _lineBuffer.Clear();
        }
    }

    public class LoggerPSHostRawUserInterface : PSHostRawUserInterface
    {
        public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
        public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
        public override Coordinates CursorPosition { get; set; } = new(0, 0);
        public override Coordinates WindowPosition { get; set; } = new(0, 0);
        public override int CursorSize { get; set; } = 25;
        public override Size BufferSize { get; set; } = new(120, 9999);
        public override Size WindowSize { get; set; } = new(120, 40);
        public override Size MaxWindowSize => new(120, 40);
        public override Size MaxPhysicalWindowSize => new(120, 40);
        public override string WindowTitle { get; set; } = "TermKit";
        public override bool KeyAvailable => false;

        public override void FlushInputBuffer() { }

        public override BufferCell[,] GetBufferContents(Rectangle rectangle) => new BufferCell[0, 0];

        public override KeyInfo ReadKey(ReadKeyOptions options) => new();

        public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }

        public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }

        public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
    }
}
