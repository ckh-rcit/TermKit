using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using TermKit.Services;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

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
        private readonly ObservableCollection<BatchItem> _batchItems = new();
        private CancellationTokenSource? _batchStop;
        private bool _connectionOperationRunning;
        private string _lastAdAdminUser = string.Empty;
        private UpdateCheckResult? _availableUpdate;
        private string? _autoNoRetentionDate;

        public MainWindow()
        {
            InitializeComponent();
            Title = $"TermKit v{GetAppVersion()}";
            ListViewBatch.ItemsSource = _batchItems;

            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "toolbox.ico"));
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1240, 600));
            ApplyDarkTitleBar();

            _config = ConfigService.Load();
            _authService = new AuthService(_config.Settings.GraphApi.Scopes.ToArray(), GetWindowHandle);
            _graphService = new GraphService(_authService, _config.Settings.GraphApi.Scopes.ToArray());
            _serviceNowService = _config.Settings.ServiceNow != null ? new ServiceNowService(_config.Settings.ServiceNow, GetTenantId) : null;

            Logger.LogEntryWritten += OnLogEntryWritten;
            Closed += (_, _) =>
            {
                Logger.LogEntryWritten -= OnLogEntryWritten;
                _adService.Disconnect();
                // A lingering windowless process makes the next Start-menu launch show nothing, so always end the process.
                _ = Task.Run(async () =>
                {
                    try { await _exo.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
                    catch (Exception exception) { Logger.Log($"Session shutdown: {exception.Message}", LogType.Warning); }
                    Environment.Exit(0);
                });
            };

            UpdateConnectionStatus();
            Logger.Log("TermKit retention workflow loaded. Connect AD, EXO, and Graph to begin.");
            AppWindow.Closing += (_, args) => args.Cancel = _retentionConversionRunning || _connectionOperationRunning;
            _ = CheckForUpdatesAsync(userInitiated: false);
        }

        private static string GetAppVersion()
        {
            var package = GetCurrentPackage();
            if (package != null) return $"{package.Value.Version.Major}.{package.Value.Version.Minor}.{package.Value.Version.Build}";
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "development";
        }

        private static (string Name, Version Version)? GetCurrentPackage()
        {
            try
            {
                var id = Windows.ApplicationModel.Package.Current.Id;
                return (id.Name, new Version(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision));
            }
            catch (InvalidOperationException)
            {
                return null; // Not running from an installed package, so there is nothing to update.
            }
        }

        private async Task CheckForUpdatesAsync(bool userInitiated)
        {
            var feed = _config.Settings.UpdateFeedUrl;
            if (string.IsNullOrWhiteSpace(feed))
            {
                if (userInitiated) await ShowMessageAsync("No update feed is configured. Set Settings.UpdateFeedUrl in config.json.", "Check for Updates");
                return;
            }

            var package = GetCurrentPackage();
            if (package == null)
            {
                if (userInitiated) await ShowMessageAsync("Updates apply to the installed package; this copy is running unpackaged.", "Check for Updates");
                return;
            }

            ButtonUpdates.IsEnabled = false;
            try
            {
                var result = await UpdateChecker.CheckAsync(feed, package.Value.Name, package.Value.Version);
                if (result.Status == UpdateStatus.Available)
                {
                    _availableUpdate = result;
                    ButtonUpdates.Content = $"Update to v{result.LatestVersion}";
                    ButtonUpdates.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
                    Logger.Log(result.Message, LogType.Success);
                }
                else
                {
                    _availableUpdate = null;
                    ButtonUpdates.Content = "Check for updates";
                    ButtonUpdates.ClearValue(FrameworkElement.StyleProperty);
                    Logger.Log(result.Message, result.Status == UpdateStatus.Failed ? LogType.Warning : LogType.Info);
                    if (userInitiated) await ShowMessageAsync(result.Message, result.Status == UpdateStatus.Failed ? "Update Check Failed" : "Check for Updates");
                }
            }
            finally
            {
                ButtonUpdates.IsEnabled = !(_retentionConversionRunning || _connectionOperationRunning);
            }
        }

        private async void ButtonUpdates_Click(object sender, RoutedEventArgs e)
        {
            if (_availableUpdate == null)
            {
                await CheckForUpdatesAsync(userInitiated: true);
                return;
            }

            if (!UpdateChecker.TryGetFeedUri(_config.Settings.UpdateFeedUrl, out var feedUri)) return;
            var confirmed = await ShowConfirmAsync($"TermKit {_availableUpdate.LatestVersion} is available. Open the update feed at {feedUri.Host}? Windows App Installer will validate the package signature and update TermKit; it will close this window.", "Update TermKit");
            if (confirmed) await Windows.System.Launcher.LaunchUriAsync(feedUri);
        }

        private IntPtr GetWindowHandle() => WindowNative.GetWindowHandle(this);

        // Key Vault sign-in is scoped to the tenant; EXO connects first, so it can supply it before Graph.
        private string? GetTenantId() => _authService.ConnectedTenantId ?? _exo.ConnectedTenantId;

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

            SetConnectionIndicator(AdConnectionStatus, AdOnlineIndicator, AdOfflineIndicator, TextBlockAdStatus, "AD", _adService.IsConnected, adStatus);
            SetConnectionIndicator(ExoConnectionStatus, ExoOnlineIndicator, ExoOfflineIndicator, TextBlockExoStatus, "EXO", _exo.IsConnected, exoStatus);
            SetConnectionIndicator(GraphConnectionStatus, GraphOnlineIndicator, GraphOfflineIndicator, TextBlockGraphStatus, "Graph", _authService.IsGraphConnected, graphStatus);

            ButtonConnectAd.IsEnabled = !busy && !_adService.IsConnected;
            ButtonDisconnectAd.IsEnabled = !busy && _adService.IsConnected;
            ButtonForgetAdCredentials.IsEnabled = !busy && HasSavedAdCredentials();
            ButtonConnectExo.IsEnabled = !busy && !_exo.IsConnected;
            ButtonConnectGraph.IsEnabled = !busy && _exo.IsConnected && !_authService.IsGraphConnected;
            ButtonDisconnect.IsEnabled = !busy;
            ButtonOpenSettings.IsEnabled = !busy;
            ButtonUpdates.IsEnabled = !busy;
            ButtonRetentionLookup.IsEnabled = !busy && _config.Settings.ServiceNow?.Enabled == true;
            UpdateBatchControls();

            var canRunRetention = _adService.IsConnected && _exo.IsConnected && _authService.IsGraphConnected && !busy;
            ButtonRetentionProcess.IsEnabled = canRunRetention;
            ButtonRetentionCopySummary.IsEnabled = !_retentionConversionRunning && !string.IsNullOrWhiteSpace(_retentionSummaryText);
            TextBlockRetentionStatus.Text = _retentionConversionRunning
                ? "Status: Processing conversion..."
                : canRunRetention
                    ? "Status: Ready. All required services are connected."
                    : "Status: Connect AD, EXO, and Graph to begin.";
        }

        private static void SetConnectionIndicator(FrameworkElement container, Ellipse online, Ellipse offline, TextBlock label, string name, bool connected, string detail)
        {
            label.Text = connected ? $"{name} connected" : $"{name} offline";
            online.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
            offline.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
            ToolTipService.SetToolTip(container, detail);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(container, detail);
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

        private string AdCredentialTarget => $"TermKit:AD:{_config.Settings.ActiveDirectory?.DomainNetbios}";

        private bool HasSavedAdCredentials()
        {
            try { return CredentialStore.Exists(AdCredentialTarget); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or DllNotFoundException)
            {
                return false;
            }
        }

        private bool TryReadSavedAdCredentials(out string user, out string password)
        {
            try { return CredentialStore.TryRead(AdCredentialTarget, out user, out password); }
            catch (System.ComponentModel.Win32Exception exception)
            {
                Logger.Log($"Could not read the saved AD credentials: {exception.Message}", LogType.Warning);
                user = password = string.Empty;
                return false;
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

            if (AdControllerDiscovery.IsSamplePlaceholder(adSettings))
            {
                throw new InvalidOperationException(AdControllerDiscovery.SamplePlaceholderMessage);
            }

            // One attempt with the saved password per click, never automatically at startup, to avoid account lockouts.
            string? notice = null;
            var hadSavedCredentials = false;
            if (TryReadSavedAdCredentials(out var savedUser, out var savedPassword))
            {
                hadSavedCredentials = true;
                _lastAdAdminUser = savedUser;
                TextBlockRetentionStatus.Text = "Status: Connecting to AD with saved credentials...";
                var savedConnected = await _adService.ConnectAsync(savedUser, savedPassword, adSettings);
                savedPassword = string.Empty;
                if (savedConnected)
                {
                    Logger.Log($"Connected to AD as '{_adService.ConnectedIdentity}' using saved credentials.", LogType.Success);
                    UpdateConnectionStatus();
                    return;
                }

                if (!_adService.LastFailureWasCredentialRejection)
                {
                    Logger.Log("Failed to connect to AD with the saved credentials.", LogType.Error);
                    throw new InvalidOperationException(_adService.LastError ?? "AD connection failed. Verify domain controllers and the RSAT Active Directory tools.");
                }

                Logger.Log($"Saved AD credentials for '{savedUser}' were rejected.", LogType.Warning);
                notice = $"The saved credentials for '{savedUser}' were rejected. If your password changed, enter the new one.";
            }

            var credentials = await PromptForAdCredentialsAsync(notice, hadSavedCredentials);
            if (credentials == null)
            {
                return;
            }

            TextBlockRetentionStatus.Text = "Status: Connecting to AD...";
            var connected = await _adService.ConnectAsync(credentials.Value.User, credentials.Value.Password, adSettings);
            if (connected)
            {
                Logger.Log($"Connected to AD as '{_adService.ConnectedIdentity}'.", LogType.Success);
                SaveOrForgetAdCredentials(credentials.Value);
            }
            else
            {
                Logger.Log("Failed to connect to AD with configured domain controllers.", LogType.Error);
                throw new InvalidOperationException(_adService.LastError ?? "AD connection failed. Verify credentials, domain controllers and the RSAT Active Directory tools.");
            }

            UpdateConnectionStatus();
        }

        private void SaveOrForgetAdCredentials((string User, string Password, bool Remember) credentials)
        {
            try
            {
                if (credentials.Remember)
                {
                    CredentialStore.Write(AdCredentialTarget, credentials.User, credentials.Password);
                    Logger.Log($"Saved AD credentials for '{credentials.User}' in Windows Credential Manager.");
                }
                else if (HasSavedAdCredentials())
                {
                    CredentialStore.Delete(AdCredentialTarget);
                    Logger.Log("Removed the saved AD credentials.");
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or ArgumentException)
            {
                Logger.Log($"Could not update the saved AD credentials: {exception.Message}", LogType.Warning);
            }
        }

        private async Task<(string User, string Password, bool Remember)?> PromptForAdCredentialsAsync(string? notice, bool rememberByDefault)
        {
            var userBox = new TextBox { Header = "Your Admin SamAccountName", Text = _lastAdAdminUser, PlaceholderText = "admin.sam" };
            var passwordBox = new PasswordBox { Header = "Your Admin Password (AD)" };
            var rememberBox = new CheckBox { Content = "Remember on this PC (Windows Credential Manager)", IsChecked = rememberByDefault };
            var errorBlock = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
            if (!string.IsNullOrWhiteSpace(notice))
            {
                panel.Children.Add(new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange) });
            }
            panel.Children.Add(userBox);
            panel.Children.Add(passwordBox);
            panel.Children.Add(rememberBox);
            panel.Children.Add(errorBlock);

            var dialog = new ContentDialog
            {
                Title = "Connect Active Directory",
                Content = panel,
                PrimaryButtonText = "Connect",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(userBox.Text) || string.IsNullOrEmpty(passwordBox.Password))
                {
                    args.Cancel = true;
                    errorBlock.Text = "Admin SamAccountName and Password are required.";
                    errorBlock.Visibility = Visibility.Visible;
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            _lastAdAdminUser = userBox.Text.Trim();
            return (_lastAdAdminUser, passwordBox.Password, rememberBox.IsChecked == true);
        }

        private void ButtonForgetAdCredentials_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CredentialStore.Delete(AdCredentialTarget);
                Logger.Log("Removed the saved AD credentials.", LogType.Success);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                Logger.Log($"Could not remove the saved AD credentials: {exception.Message}", LogType.Error);
            }
            UpdateConnectionStatus();
        }

        private void ButtonDisconnectAd_Click(object sender, RoutedEventArgs e)
        {
            if (_connectionOperationRunning || _retentionConversionRunning) return;
            _adService.Disconnect();
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

            ButtonConnectGraph.IsEnabled = false;
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
            UpdateConnectionStatus();
        }

        private void CheckBoxNoRetention_Changed(object sender, RoutedEventArgs e)
        {
            if (TextBoxRetentionEndDate == null || TextBlockRetentionDelegateLabel == null) return;

            if (CheckBoxNoRetention.IsChecked == true)
            {
                _autoNoRetentionDate = RetentionRules.NoRetentionEndDate(DateTime.Today).ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                TextBoxRetentionEndDate.Text = _autoNoRetentionDate;
                TextBlockRetentionDelegateLabel.Text = "Delegate (optional):";
            }
            else
            {
                // Only discard the date this checkbox filled in, never one the user typed.
                if (_autoNoRetentionDate != null && TextBoxRetentionEndDate.Text.Trim() == _autoNoRetentionDate) TextBoxRetentionEndDate.Text = string.Empty;
                _autoNoRetentionDate = null;
                TextBlockRetentionDelegateLabel.Text = "Delegate (Sam/UPN):";
            }
        }

        private async void ButtonRetentionLookup_Click(object sender, RoutedEventArgs e)
            => await RunConnectionOperationAsync(LookUpTaskAsync);

        private async Task LookUpTaskAsync()
        {
            var ticket = TextBoxRetentionTaskNumber.Text.Trim();
            if (string.IsNullOrWhiteSpace(ticket))
            {
                await ShowMessageAsync("Enter a Task # first.", "Task Lookup");
                return;
            }

            if (_serviceNowService == null || _config.Settings.ServiceNow?.Enabled != true)
            {
                await ShowMessageAsync("ServiceNow integration is disabled. Enable it in Settings.", "Task Lookup");
                return;
            }

            if (!Guid.TryParse(GetTenantId(), out _))
            {
                await ShowMessageAsync("Connect Exchange Online (or Microsoft Graph) first so the Key Vault sign-in uses the right tenant.", "Task Lookup");
                return;
            }

            TextBlockRetentionStatus.Text = $"Status: Reading {ticket} from ServiceNow...";
            Logger.Log($"[ServiceNow] Reading '{ticket}' with the service account...");
            var result = await _serviceNowService.LookupTaskAsync(ticket);
            if (!result.Success || result.Details == null)
            {
                Logger.Log($"[ServiceNow] {result.Message}", LogType.Error);
                await ShowMessageAsync(result.Message, "Task Lookup Failed");
                return;
            }

            var details = result.Details;
            if (details.RetainEmail == false)
            {
                CheckBoxNoRetention.IsChecked = true;
                Logger.Log("[ServiceNow] The request says email retention is not needed: end date set to tomorrow and the delegate is optional.");
            }
            else if (details.RetainEmail == true)
            {
                CheckBoxNoRetention.IsChecked = false;
            }

            if (details.TermUser != null) TextBoxRetentionTargetUser.Text = details.TermUser;
            if (details.DelegateUser != null) TextBoxRetentionDelegateUser.Text = details.DelegateUser;
            if (details.RetentionEndDate != null && details.RetainEmail != false) TextBoxRetentionEndDate.Text = details.RetentionEndDate.Value.ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);

            Logger.Log($"[ServiceNow] {details.TicketNumber}: {details.ShortDescription}", LogType.Success);
            Logger.Log($"[ServiceNow] Term user: {details.TermUser ?? "(not found)"}; delegate: {details.DelegateUser ?? "(not found)"}{(string.IsNullOrWhiteSpace(details.DelegateName) ? string.Empty : $" ({details.DelegateName})")}; retention end: {details.RetentionEndDate:MM/dd/yyyy}");
            foreach (var warning in details.Warnings) Logger.Log($"[ServiceNow] {warning}", LogType.Warning);

            if (details.Warnings.Count > 0)
            {
                await ShowMessageAsync(string.Join(Environment.NewLine, details.Warnings) + Environment.NewLine + Environment.NewLine + "Review and complete the fields before processing.", "Task Lookup: Review Needed");
            }
        }

        private void SelectorBarMode_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (PanelSingle == null || PanelBatch == null) return;
            var batch = sender.SelectedItem == SelectorItemBatch;
            PanelSingle.Visibility = batch ? Visibility.Collapsed : Visibility.Visible;
            PanelBatch.Visibility = batch ? Visibility.Visible : Visibility.Collapsed;
            LeftColumn.Width = new GridLength(batch ? 780 : 640);
        }

        private void UpdateBatchControls()
        {
            var busy = _retentionConversionRunning || _connectionOperationRunning;
            var connected = _adService.IsConnected && _exo.IsConnected && _authService.IsGraphConnected;
            ButtonBatchLookup.IsEnabled = !busy && _config.Settings.ServiceNow?.Enabled == true;
            ButtonBatchProcess.IsEnabled = !busy && connected && _batchItems.Any(item => item.Status == BatchItemStatus.Ready);
            ButtonBatchStop.IsEnabled = _batchStop != null;
            ButtonBatchClear.IsEnabled = !busy;
            ButtonBatchCopy.IsEnabled = !busy && _batchItems.Any(item => !string.IsNullOrEmpty(item.Summary));
            TextBoxBatchTasks.IsEnabled = !busy;
            NumberBoxBatchConcurrency.IsEnabled = !busy;
        }

        private async void ButtonBatchLookup_Click(object sender, RoutedEventArgs e)
            => await RunConnectionOperationAsync(BatchLookupAsync);

        private async Task BatchLookupAsync()
        {
            if (_serviceNowService == null || _config.Settings.ServiceNow?.Enabled != true)
            {
                await ShowMessageAsync("ServiceNow integration is disabled. Enable it in Settings.", "Batch Lookup");
                return;
            }

            if (!Guid.TryParse(GetTenantId(), out _))
            {
                await ShowMessageAsync("Connect Exchange Online (or Microsoft Graph) first so the Key Vault sign-in uses the right tenant.", "Batch Lookup");
                return;
            }

            var tasks = BatchPlanner.ParseTaskNumbers(TextBoxBatchTasks.Text, out var rejected);
            if (rejected.Count > 0) Logger.Log($"[Batch] Ignored invalid entries: {string.Join(", ", rejected)}", LogType.Warning);
            if (tasks.Count == 0)
            {
                await ShowMessageAsync("Enter at least one task number.", "Batch Lookup");
                return;
            }

            if (tasks.Count > BatchPlanner.MaxItems)
            {
                await ShowMessageAsync($"A batch is limited to {BatchPlanner.MaxItems} tasks; {tasks.Count} were entered.", "Batch Lookup");
                return;
            }

            _batchItems.Clear();
            foreach (var task in tasks) _batchItems.Add(new BatchItem(task) { Message = "Waiting to be looked up." });

            var index = 0;
            foreach (var item in _batchItems.ToList())
            {
                index++;
                TextBlockBatchStatus.Text = $"Looking up {item.TaskNumber} ({index} of {tasks.Count})...";
                item.Message = "Looking up...";
                var lookup = await _serviceNowService.LookupTaskAsync(item.TaskNumber);
                BatchPlanner.ApplyLookup(item, lookup, DateTime.Today);
            }

            BatchPlanner.FlagDuplicates(_batchItems);
            var ready = _batchItems.Count(item => item.Status == BatchItemStatus.Ready);
            var attention = _batchItems.Count - ready;
            TextBlockBatchStatus.Text = $"{ready} ready, {attention} need attention (skipped; fix them in Single mode)."
                + (_adService.IsConnected && _exo.IsConnected && _authService.IsGraphConnected ? string.Empty : " Connect AD, EXO, and Graph to process.");
            Logger.Log($"[Batch] Looked up {tasks.Count} task(s): {ready} ready, {attention} need attention.", attention == 0 ? LogType.Success : LogType.Warning);
        }

        private async void ButtonBatchProcess_Click(object sender, RoutedEventArgs e)
        {
            if (_retentionConversionRunning || _connectionOperationRunning) return;
            if (!_adService.IsConnected || !_exo.IsConnected || !_authService.IsGraphConnected)
            {
                await ShowMessageAsync("Connect AD, EXO, and Graph before running the batch.", "Connection Required");
                return;
            }

            var ready = _batchItems.Where(item => item.Status == BatchItemStatus.Ready).ToList();
            if (ready.Count == 0)
            {
                await ShowMessageAsync("No rows are ready to process.", "Process Batch");
                return;
            }

            var value = NumberBoxBatchConcurrency.Value;
            var concurrency = Math.Clamp(double.IsNaN(value) ? 3 : (int)value, 1, 5);
            var skipped = _batchItems.Count - ready.Count;
            var confirmed = await ShowConfirmAsync(
                $"Process {ready.Count} termination(s)? {skipped} row(s) will be skipped. Up to {concurrency} run at once and each takes roughly 15 minutes. Changes are not rolled back automatically.",
                "Process Batch");
            if (!confirmed) return;

            _retentionConversionRunning = true;
            _batchStop = new CancellationTokenSource();
            UpdateConnectionStatus();
            Logger.Log($"[Batch] Starting {ready.Count} termination(s), {concurrency} at a time.");
            try
            {
                var service = new RetentionConversionService(_adService, _exo, _graphService, _config, _serviceNowService);
                var confirmGate = new SemaphoreSlim(1, 1);
                async Task<bool> ConfirmFor(string task, string prompt)
                {
                    // Only one dialog can be open at a time.
                    await confirmGate.WaitAsync();
                    try { return await ShowConfirmAsync($"{task}: {prompt}", "Warning: Mailbox Exceeds 50 GB"); }
                    finally { confirmGate.Release(); }
                }

                await BatchRunner.RunAsync(_batchItems.ToList(), concurrency, async item =>
                {
                    using (Logger.BeginScope(item.TaskNumber))
                    {
                        return await service.ProcessAsync(item.ToRequest(), prompt => ConfirmFor(item.TaskNumber, prompt));
                    }
                }, _batchStop.Token);

                var succeeded = _batchItems.Count(item => item.Status == BatchItemStatus.Succeeded);
                var failed = _batchItems.Count(item => item.Status == BatchItemStatus.Failed);
                var notStarted = _batchItems.Count(item => item.Status == BatchItemStatus.NotStarted);
                var finished = $"Batch finished: {succeeded} done, {failed} failed, {notStarted} not started, {skipped} skipped.";
                TextBlockBatchStatus.Text = finished;
                Logger.Log($"[Batch] {finished}", failed == 0 ? LogType.Success : LogType.Warning);
                await ShowMessageAsync(finished, "Batch Complete");
            }
            catch (Exception exception)
            {
                ErrorReporter.Report("Batch failed", exception);
            }
            finally
            {
                _batchStop.Dispose();
                _batchStop = null;
                _retentionConversionRunning = false;
                UpdateConnectionStatus();
            }
        }

        private void ButtonBatchStop_Click(object sender, RoutedEventArgs e)
        {
            _batchStop?.Cancel();
            ButtonBatchStop.IsEnabled = false;
            TextBlockBatchStatus.Text = "Stopping: terminations already running will finish; queued ones will not start.";
            Logger.Log("[Batch] Stop requested. Running terminations finish; queued ones will not start.", LogType.Warning);
        }

        private async void ButtonBatchCopy_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join(Environment.NewLine + Environment.NewLine,
                _batchItems.Where(item => !string.IsNullOrEmpty(item.Summary)).Select(item => $"{item.TaskNumber} ({item.TermUser}):{Environment.NewLine}{item.Summary}"));
            if (text.Length == 0)
            {
                await ShowMessageAsync("No summaries are available yet.", "Copy Summaries");
                return;
            }

            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Logger.Log("[Batch] Summaries copied to clipboard.", LogType.Success);
        }

        private void ButtonBatchClear_Click(object sender, RoutedEventArgs e)
        {
            if (_retentionConversionRunning || _connectionOperationRunning) return;
            _batchItems.Clear();
            TextBlockBatchStatus.Text = "Paste task numbers, then Look up all.";
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
                RetentionDateText = TextBoxRetentionEndDate.Text,
                NoRetention = CheckBoxNoRetention.IsChecked == true
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
                    var tester = new ServiceNowService(tempConfig, GetTenantId);
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
                    MinWidth = 700,
                    MaxHeight = 520
                }
            };
            // ContentDialog caps its width at 548 unless this theme resource is raised.
            dialog.Resources["ContentDialogMaxWidth"] = 820.0;

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
                _serviceNowService = new ServiceNowService(serviceNow, GetTenantId);
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
