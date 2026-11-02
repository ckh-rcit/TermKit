using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using TermKit.Services;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace TermKit
{
    /// <summary>
    /// Main window for the TermKit retention conversion workflow.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly ToolConfig _config;
        private readonly ExoPowerShellService _exo = new();
        private readonly OnPremAdService _adService = new();
        private AuthService _authService;
        private GraphService _graphService;
        private ServiceNowService? _serviceNowService;

        private bool _showWarnings;
        private bool _retentionConversionRunning;
        private string _retentionSummaryText = string.Empty;
        private bool _connectionOperationRunning;

        public MainWindow()
        {
            InitializeComponent();

            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "toolbox.ico"));
            ApplyDarkTitleBar();

            _config = ConfigService.Load();
            _authService = new AuthService(_config.Settings.GraphApi.Scopes.ToArray(), GetWindowHandle);
            _graphService = new GraphService(_authService, _config.Settings.GraphApi.Scopes.ToArray());
            _serviceNowService = _config.Settings.ServiceNow != null ? new ServiceNowService(_config.Settings.ServiceNow, () => _authService.ConnectedTenantId) : null;

            Logger.LogEntryWritten += OnLogEntryWritten;
            Closed += async (_, _) =>
            {
                Logger.LogEntryWritten -= OnLogEntryWritten;
                _adService.Disconnect();
                try { await _exo.ShutdownAsync(); }
                catch (Exception exception) { Logger.Log($"Session shutdown: {exception.Message}", LogType.Warning); }
            };

            UpdateConnectionStatus();
            Logger.Log("TermKit retention workflow loaded. Connect AD, EXO, and Graph to begin.");
            AppWindow.Closing += (_, args) => args.Cancel = _retentionConversionRunning || _connectionOperationRunning;
        }

        private IntPtr GetWindowHandle() => WindowNative.GetWindowHandle(this);

        private void ApplyDarkTitleBar()
        {
            if (!AppWindowTitleBar.IsCustomizationSupported())
            {
                return;
            }

            var titleBar = AppWindow.TitleBar;
            titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 200, 200, 200);
            titleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
            titleBar.ButtonForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, 200, 200, 200);
            titleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 60, 60, 60);
            titleBar.ButtonHoverForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
            titleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 80, 80, 80);
            titleBar.ButtonPressedForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }

        private void UpdateConnectionStatus()
        {
            var busy = _retentionConversionRunning || _connectionOperationRunning;
            var adStatus = _adService.IsConnected ? $"AD: {_adService.ConnectedIdentity}" : "AD: Not connected";
            var exoStatus = _exo.IsConnected ? $"EXO: {_exo.ConnectedUserPrincipalName}" : "EXO: Not connected";
            var graphStatus = _authService.IsGraphConnected ? $"Graph: {_authService.ConnectedUser}" : "Graph: Not connected";

            TextBlockConnectionStatus.Text = $"{adStatus} | {exoStatus} | {graphStatus}";

            TextBlockRetentionAdStatus.Text = adStatus;
            TextBlockRetentionAdStatus.Foreground = new SolidColorBrush(_adService.IsConnected ? Microsoft.UI.Colors.LightGreen : Microsoft.UI.Colors.IndianRed);
            ButtonConnectAd.IsEnabled = !busy && !_adService.IsConnected;
            ButtonDisconnectAd.IsEnabled = !busy && _adService.IsConnected;
            TextBoxRetentionAdminUser.IsReadOnly = _adService.IsConnected;
            PasswordBoxRetentionAdminPassword.IsEnabled = !_adService.IsConnected;

            TextBlockRetentionExoStatus.Text = exoStatus;
            TextBlockRetentionExoStatus.Foreground = new SolidColorBrush(_exo.IsConnected ? Microsoft.UI.Colors.LightGreen : Microsoft.UI.Colors.IndianRed);
            ButtonRetentionConnectExo.IsEnabled = !busy && !_exo.IsConnected;

            TextBlockRetentionGraphStatus.Text = graphStatus;
            TextBlockRetentionGraphStatus.Foreground = new SolidColorBrush(_authService.IsGraphConnected ? Microsoft.UI.Colors.LightGreen : Microsoft.UI.Colors.IndianRed);
            ButtonRetentionConnectGraph.IsEnabled = !busy && _exo.IsConnected && !_authService.IsGraphConnected;
            ButtonRetentionDisconnectAll.IsEnabled = !busy;
            ButtonOpenSettings.IsEnabled = !busy;

            var canRunRetention = _adService.IsConnected && _exo.IsConnected && _authService.IsGraphConnected && !busy;
            ButtonRetentionProcess.IsEnabled = canRunRetention;
            ButtonRetentionCopySummary.IsEnabled = !_retentionConversionRunning && !string.IsNullOrWhiteSpace(_retentionSummaryText);
            TextBlockRetentionStatus.Text = _retentionConversionRunning
                ? "Status: Processing conversion..."
                : canRunRetention
                    ? "Status: Ready. All required services are connected."
                    : "Status: Connect AD, EXO, and Graph to begin.";
        }

        private async void ButtonConnectAd_Click(object sender, RoutedEventArgs e)
            => await RunConnectionOperationAsync(ConnectAdAsync);

        private async Task RunConnectionOperationAsync(Func<Task> operation)
        {
            if (_connectionOperationRunning || _retentionConversionRunning) return;
            _connectionOperationRunning = true;
            UpdateConnectionStatus();
            try { await operation(); }
            catch (Exception exception) { ErrorReporter.Report("Operation failed", exception); }
            finally
            {
                _connectionOperationRunning = false;
                UpdateConnectionStatus();
            }
        }

        private async Task ConnectAdAsync()
        {
            var adSettings = _config.Settings.ActiveDirectory;
            if (adSettings == null)
            {
                await ShowMessageAsync("ActiveDirectory settings are missing from config.json.", "Config Missing");
                return;
            }

            var username = TextBoxRetentionAdminUser.Text.Trim();
            var password = PasswordBoxRetentionAdminPassword.Password;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                await ShowMessageAsync("Admin SamAccountName and Password are required.", "Input Required");
                return;
            }

            TextBlockRetentionStatus.Text = "Status: Connecting to AD...";
            var connected = await _adService.ConnectAsync(username, password, adSettings);
            if (connected)
            {
                PasswordBoxRetentionAdminPassword.Password = string.Empty;
                Logger.Log($"Connected to AD as '{_adService.ConnectedIdentity}'.", LogType.Success);
            }
            else
            {
                Logger.Log("Failed to connect to AD with configured domain controllers.", LogType.Error);
                throw new InvalidOperationException(_adService.LastError ?? "AD connection failed. Verify credentials, domain controllers and the RSAT Active Directory tools.");
            }

            UpdateConnectionStatus();
        }

        private void ButtonDisconnectAd_Click(object sender, RoutedEventArgs e)
        {
            if (_connectionOperationRunning || _retentionConversionRunning) return;
            _adService.Disconnect();
            PasswordBoxRetentionAdminPassword.Password = string.Empty;
            Logger.Log("Disconnected from AD session.");
            UpdateConnectionStatus();
        }

        private async void ButtonConnectExo_Click(object sender, RoutedEventArgs e)
            => await RunConnectionOperationAsync(async () =>
            {
                if (!await _exo.ConnectAsync()) throw new InvalidOperationException(_exo.LastError ?? "Exchange Online sign-in failed.");
            });

        private async void ButtonConnectGraph_Click(object sender, RoutedEventArgs e)
            => await RunConnectionOperationAsync(ConnectGraphAsync);

        private async Task ConnectGraphAsync()
        {
            if (!_exo.IsConnected)
            {
                await ShowMessageAsync("Connect to Exchange Online first.", "Connection Order");
                return;
            }

            ButtonRetentionConnectGraph.IsEnabled = false;
            var connected = await _authService.ConnectGraphAsync(_exo.ConnectedTenantId);
            if (!connected) throw new InvalidOperationException(_authService.LastError ?? "Graph sign-in failed.");
            if (connected)
            {
                try
                {
                    _graphService.InitializeGraphClient();
                    await _graphService.VerifyConnectionAsync();
                    await _exo.GetAcceptedDomainsAsync();
                }
                catch
                {
                    await _authService.DisconnectGraphAsync();
                    throw;
                }
            }
            UpdateConnectionStatus();
        }

        private async void ButtonDisconnect_Click(object sender, RoutedEventArgs e)
            => await RunConnectionOperationAsync(DisconnectAsync);

        private async Task DisconnectAsync()
        {
            await _exo.DisconnectAsync();
            await _authService.DisconnectGraphAsync();
            _adService.Disconnect();
            PasswordBoxRetentionAdminPassword.Password = string.Empty;
            UpdateConnectionStatus();
        }

        private async void ButtonRetentionProcess_Click(object sender, RoutedEventArgs e)
        {
            if (_retentionConversionRunning || _connectionOperationRunning)
            {
                return;
            }

            if (!_adService.IsConnected || !_exo.IsConnected || !_authService.IsGraphConnected)
            {
                await ShowMessageAsync("Connect AD, EXO, and Graph before running conversion.", "Connection Required");
                return;
            }

            var request = new RetentionConversionRequest
            {
                TaskNumber = TextBoxRetentionTaskNumber.Text,
                TargetUserInput = TextBoxRetentionTargetUser.Text,
                DelegateInput = TextBoxRetentionDelegateUser.Text,
                RetentionDateText = TextBoxRetentionEndDate.Text
            };

            _retentionConversionRunning = true;
            _retentionSummaryText = string.Empty;
            UpdateConnectionStatus();

            try
            {
                var retentionService = new RetentionConversionService(_adService, _exo, _graphService, _config, _serviceNowService);
                var result = await retentionService.ProcessAsync(request, message => ShowConfirmAsync(message, "Warning: Mailbox Exceeds 50 GB"));
                _retentionSummaryText = result.SummaryText;
                await ShowMessageAsync("Retention conversion completed successfully.", "Process Complete");
            }
            catch (Exception ex)
            {
                ErrorReporter.Report("Retention conversion failed", ex);
            }
            finally
            {
                _retentionConversionRunning = false;
                UpdateConnectionStatus();
            }
        }

        private async void ButtonRetentionCopySummary_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_retentionSummaryText))
            {
                await ShowMessageAsync("No summary text is available yet.", "Copy Summary");
                return;
            }

            var package = new DataPackage();
            package.SetText(_retentionSummaryText);
            Clipboard.SetContent(package);
            Logger.Log("Summary copied to clipboard.", LogType.Success);
        }

        private void CheckBoxShowWarnings_Changed(object sender, RoutedEventArgs e)
        {
            if (TextBlockLog == null || LogScrollViewer == null) return;
            _showWarnings = CheckBoxShowWarnings.IsChecked == true;

            TextBlockLog.Inlines.Clear();
            foreach (var (line, type) in Logger.GetHistory())
            {
                if (type == LogType.Warning && !_showWarnings)
                {
                    continue;
                }
                AppendLogLine(line, type);
            }

            LogScrollViewer.UpdateLayout();
            LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
        }

        private void OnLogEntryWritten(string line, LogType type)
        {
            if (type == LogType.Warning && !_showWarnings)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                AppendLogLine(line, type);
                LogScrollViewer.UpdateLayout();
                LogScrollViewer.ChangeView(null, LogScrollViewer.ScrollableHeight, null, true);
            });
        }

        private void AppendLogLine(string line, LogType type)
        {
            TextBlockLog.Inlines.Add(new Run
            {
                Text = line + Environment.NewLine,
                Foreground = new SolidColorBrush(Logger.GetColorForType(type))
            });
        }

        private async void ButtonOpenSettings_Click(object sender, RoutedEventArgs e)
            => await RunConnectionOperationAsync(OpenSettingsAsync);

        private async Task OpenSettingsAsync()
        {
            var current = _config.Settings.ServiceNow ?? new ServiceNowConfig();

            var enabledCheck = new CheckBox { Content = "Enable ServiceNow integration", IsChecked = current.Enabled };
            var instanceBox = new TextBox { Text = current.InstanceUrl, PlaceholderText = "https://your-instance.service-now.com/" };
            var keyVaultBox = new TextBox { Text = current.KeyVaultUrl, PlaceholderText = "https://your-keyvault.vault.azure.net/" };
            var subscriptionBox = new TextBox { Text = current.SubscriptionId, PlaceholderText = "Subscription Id" };
            var usernameSecretBox = new TextBox { Text = current.UsernameSecretName, PlaceholderText = "Username secret name" };
            var passwordSecretBox = new TextBox { Text = current.PasswordSecretName, PlaceholderText = "Password secret name" };
            var tableBox = new TextBox { Text = string.IsNullOrWhiteSpace(current.Table) ? "task" : current.Table };
            var ticketFieldBox = new TextBox { Text = string.IsNullOrWhiteSpace(current.TicketNumberField) ? "number" : current.TicketNumberField };
            var closeStateFieldBox = new TextBox { Text = string.IsNullOrWhiteSpace(current.CloseStateField) ? "state" : current.CloseStateField };
            var closeStateValueBox = new TextBox { Text = string.IsNullOrWhiteSpace(current.CloseStateValue) ? "3" : current.CloseStateValue };
            var workNotesFieldBox = new TextBox { Text = string.IsNullOrWhiteSpace(current.WorkNotesField) ? "work_notes" : current.WorkNotesField };
            var commentsFieldBox = new TextBox { Text = string.IsNullOrWhiteSpace(current.AdditionalCommentsField) ? "comments" : current.AdditionalCommentsField };
            var updateTableBox = new TextBox { Text = current.UpdateTable };
            var statusBlock = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.WrapWholeWords };

            var testButton = new Button { Content = "Test Connection", HorizontalAlignment = HorizontalAlignment.Left };
            testButton.Click += async (_, _) =>
            {
                statusBlock.Text = "Testing ServiceNow connection...";
                var tempConfig = new ServiceNowConfig
                {
                    Enabled = enabledCheck.IsChecked == true,
                    InstanceUrl = instanceBox.Text.Trim(),
                    KeyVaultUrl = keyVaultBox.Text.Trim(),
                    SubscriptionId = subscriptionBox.Text.Trim(),
                    UsernameSecretName = usernameSecretBox.Text.Trim(),
                    PasswordSecretName = passwordSecretBox.Text.Trim(),
                    Table = tableBox.Text.Trim(),
                    TicketNumberField = ticketFieldBox.Text.Trim(),
                    CloseStateField = closeStateFieldBox.Text.Trim(),
                    CloseStateValue = closeStateValueBox.Text.Trim(),
                    WorkNotesField = workNotesFieldBox.Text.Trim(),
                    AdditionalCommentsField = commentsFieldBox.Text.Trim(),
                    UpdateTable = updateTableBox.Text.Trim()
                };

                testButton.IsEnabled = false;
                try
                {
                    var tester = new ServiceNowService(tempConfig, () => _authService.ConnectedTenantId);
                    var result = await tester.TestConnectionAsync();
                    statusBlock.Text = result.Message;
                }
                catch (Exception exception)
                {
                    Logger.Log($"ServiceNow connection test failed: {exception}", LogType.Error);
                    statusBlock.Text = exception.Message;
                }
                finally { testButton.IsEnabled = true; }
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(enabledCheck);
            panel.Children.Add(new TextBlock { Text = "Instance URL" });
            panel.Children.Add(instanceBox);
            panel.Children.Add(new TextBlock { Text = "Key Vault URL" });
            panel.Children.Add(keyVaultBox);
            panel.Children.Add(new TextBlock { Text = "Subscription Id" });
            panel.Children.Add(subscriptionBox);
            panel.Children.Add(new TextBlock { Text = "Username Secret Name" });
            panel.Children.Add(usernameSecretBox);
            panel.Children.Add(new TextBlock { Text = "Password Secret Name" });
            panel.Children.Add(passwordSecretBox);
            panel.Children.Add(new TextBlock { Text = "Table" });
            panel.Children.Add(tableBox);
            panel.Children.Add(new TextBlock { Text = "Ticket Number Field" });
            panel.Children.Add(ticketFieldBox);
            panel.Children.Add(new TextBlock { Text = "Close State Field" });
            panel.Children.Add(closeStateFieldBox);
            panel.Children.Add(new TextBlock { Text = "Close State Value" });
            panel.Children.Add(closeStateValueBox);
            panel.Children.Add(new TextBlock { Text = "Work Notes Field" });
            panel.Children.Add(workNotesFieldBox);
            panel.Children.Add(new TextBlock { Text = "Additional Comments Field" });
            panel.Children.Add(commentsFieldBox);
            panel.Children.Add(new TextBlock { Text = "Update Table (optional)" });
            panel.Children.Add(updateTableBox);
            panel.Children.Add(testButton);
            panel.Children.Add(statusBlock);

            var dialog = new ContentDialog
            {
                Title = "ServiceNow Settings",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
                Content = new ScrollViewer
                {
                    Content = panel,
                    MaxHeight = 520
                }
            };

            var resultDialog = await dialog.ShowAsync();
            if (resultDialog != ContentDialogResult.Primary)
            {
                return;
            }

            var serviceNow = _config.Settings.ServiceNow ?? new ServiceNowConfig();
            serviceNow.Enabled = enabledCheck.IsChecked == true;
            serviceNow.InstanceUrl = instanceBox.Text.Trim();
            serviceNow.KeyVaultUrl = keyVaultBox.Text.Trim();
            serviceNow.SubscriptionId = subscriptionBox.Text.Trim();
            serviceNow.UsernameSecretName = usernameSecretBox.Text.Trim();
            serviceNow.PasswordSecretName = passwordSecretBox.Text.Trim();
            serviceNow.Table = string.IsNullOrWhiteSpace(tableBox.Text) ? "task" : tableBox.Text.Trim();
            serviceNow.TicketNumberField = string.IsNullOrWhiteSpace(ticketFieldBox.Text) ? "number" : ticketFieldBox.Text.Trim();
            serviceNow.CloseStateField = string.IsNullOrWhiteSpace(closeStateFieldBox.Text) ? "state" : closeStateFieldBox.Text.Trim();
            serviceNow.CloseStateValue = string.IsNullOrWhiteSpace(closeStateValueBox.Text) ? "3" : closeStateValueBox.Text.Trim();
            serviceNow.WorkNotesField = string.IsNullOrWhiteSpace(workNotesFieldBox.Text) ? "work_notes" : workNotesFieldBox.Text.Trim();
            serviceNow.AdditionalCommentsField = string.IsNullOrWhiteSpace(commentsFieldBox.Text) ? "comments" : commentsFieldBox.Text.Trim();
            serviceNow.UpdateTable = updateTableBox.Text.Trim();

            _config.Settings.ServiceNow = serviceNow;

            try
            {
                ConfigService.Save(_config);
                _serviceNowService = new ServiceNowService(serviceNow, () => _authService.ConnectedTenantId);
                Logger.Log("ServiceNow settings saved.", LogType.Success);
            }
            catch (Exception ex)
            {
                Logger.Log($"Failed to save ServiceNow settings: {ex.Message}", LogType.Error);
                await ShowMessageAsync($"Failed to save settings: {ex.Message}", "Settings Save Failed");
            }
        }

        private async Task ShowMessageAsync(string message, string title)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dialog.ShowAsync();
        }

        private async Task<bool> ShowConfirmAsync(string message, string title)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "Yes",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
    }
}
