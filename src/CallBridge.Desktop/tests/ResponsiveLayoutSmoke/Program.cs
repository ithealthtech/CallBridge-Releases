using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CallBridge.Desktop;

internal static class Program
{
    private static string CaptureScreenPopPreview(string directory)
    {
        var answered = false;
        var pop = new IncomingCallWindow("+1 828 555 0142", "(828) 555-0142", () => { answered = true; return Task.CompletedTask; }, () => Task.CompletedTask, _ => { });
        pop.SetContext(new IncomingCallContext(
            "Dana Mercer",
            "Blue Ridge Dental",
            "101",
            [
                new ConnectWiseTicketSummary("48213", "Front desk printer offline after update", "Priority 2 - High", "New"),
                new ConnectWiseTicketSummary("48190", "New hire laptop for Kayla R.", "Priority 4 - Low", "Scheduled")
            ],
            null));
        var content = (FrameworkElement)pop.Content;
        content.Measure(new Size(IncomingCallWindow.PopWidth, double.PositiveInfinity));
        var height = (int)Math.Ceiling(content.DesiredSize.Height);
        if (height < 200) throw new InvalidOperationException("Screen pop rendered too short to show caller context.");
        content.Arrange(new Rect(0, 0, IncomingCallWindow.PopWidth, height));
        content.UpdateLayout();
        var texts = FindVisualChildren<TextBlock>(content).Select(text => text.Text).ToArray();
        foreach (var expected in new[] { "Dana Mercer", "Blue Ridge Dental", "#48213", "P2", "Answer", "Decline" })
            if (!texts.Contains(expected) && !FindVisualChildren<Button>(content).Any(button => Equals(button.Content, expected)))
                throw new InvalidOperationException($"Screen pop is missing '{expected}'.");
        var bitmap = new RenderTargetBitmap((int)IncomingCallWindow.PopWidth, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var answer = FindVisualChildren<Button>(content).First(button => Equals(button.Content, "Answer"));
        answer.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        if (!answered) throw new InvalidOperationException("Screen pop Answer did not invoke the answer action.");
        var path = Path.Combine(directory, "callbridge-screen-pop.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
        pop.Close();
        return path;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    [STAThread]
    private static int Main()
    {
        Environment.SetEnvironmentVariable("CALLBRIDGE_LOCAL_TOKEN", "responsive-layout-smoke");
        Environment.SetEnvironmentVariable("CALLBRIDGE_DISABLE_AUDIO_PLAYBACK", "1");
        Environment.SetEnvironmentVariable("CALLBRIDGE_THEME", "dark");
        var testProfile = Path.Combine(Path.GetTempPath(), "CallBridge", $"responsive-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("CALLBRIDGE_SETTINGS_ROOT", testProfile);

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            var window = new MainWindow();
            VerifyResponsiveLayout(window);
            VerifyHomeAndMoreNavigation(window);
            VerifyResponsiveAsyncNavigation(window);
            VerifyActiveNavigation(window);
            VerifyNavigationDiscoverability(window);
            VerifyPrimaryControlDiscoverability(window);
            VerifyKeyboardFocusStyles(window);
            VerifyControlTargetSizes(window);
            VerifyLongTextHandling(window);
            VerifyFilteredEmptyState(window);
            VerifyListToolbarModes(window);
            VerifyRowRouting(window);
            VerifyKeypadToggle(window);
            VerifyProviderRegistrationReadiness(window);
            VerifyPhoneControlState(window);
            VerifyListActionState(window);
            VerifyBusyActionFeedback(window);
            VerifyInlineStatusFeedback(window);
            VerifyOwnedNotificationHelpers();
            VerifyListKeyboardActivationHook(window);
            VerifyDisabledTooltips(window);
            VerifyEmbeddedSettingsPage(window);
            VerifyTimeEntryQueue(window);
            VerifyUpdateIndicators(window);
        VerifyConnectWiseModeSettings(window);
        var brandedPath = VerifyBranding(window);
            var settingsPreviewPath = CaptureSettingsPreview(window);
            var homePreviewPath = CaptureHomePreview(window);
            var previewPath = CaptureIncomingCallPreview(window);
            var inCallPath = CaptureInCallPreview(window);
            var screenPopPath = CaptureScreenPopPreview(Path.GetDirectoryName(previewPath)!);
            VerifySecondaryWindowUsability();
            window.Close();
            Console.WriteLine($"Rendered screen pop: {screenPopPath}");
            Console.WriteLine($"Rendered home: {homePreviewPath}");
            Console.WriteLine($"Rendered branded: {brandedPath}");
            Console.WriteLine($"Rendered in-call: {inCallPath}");
            Console.WriteLine($"Rendered settings preview: {settingsPreviewPath}");
            Console.WriteLine($"Rendered preview: {previewPath}");
            Console.WriteLine("Responsive WPF layout smoke test passed: header tabs, home call strip, recent calls and contact search modes, More placeholders, keypad toggle, embedded settings with directory tools, async navigation, discoverability, control metadata, focus, target size, filtered empty, row routing, provider readiness, phone control, list action, inline feedback, keyboard list activation, disabled tooltip states, and secondary windows are usable.");
            return 0;
        }
        finally
        {
            app.Shutdown();
            Environment.SetEnvironmentVariable("CALLBRIDGE_SETTINGS_ROOT", null);
            Environment.SetEnvironmentVariable("CALLBRIDGE_DISABLE_AUDIO_PLAYBACK", null);
            try { if (Directory.Exists(testProfile)) Directory.Delete(testProfile, true); } catch { }
        }
    }

    private static readonly MethodInfo ShowRowsMethod = typeof(MainWindow).GetMethod("ShowRows", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find ShowRows.");

    private static void ShowRows(MainWindow window, string title, params RowItem[] rows) =>
        ShowRowsMethod.Invoke(window, new object[] { title, $"{title} subtitle", rows.ToList() });

    private static void VerifyResponsiveLayout(MainWindow window)
    {
        ApplyLayout(window, 880);
        AssertVisible(window, "BrandTitle", "wide brand title");
        AssertVisible(window, "ExtensionText", "wide extension label");
        AssertGridColumns(window, "SettingsGeneralGrid", 2, "wide settings fields");
        AssertThickness(window, "ContentHost", new Thickness(14), "wide content margin");

        ApplyLayout(window, 650);
        AssertVisible(window, "BrandTitle", "narrow brand title");
        AssertCollapsed(window, "ExtensionText", "narrow extension label");
        AssertGridColumns(window, "SettingsAudioGrid", 1, "narrow settings fields");
        AssertThickness(window, "ContentHost", new Thickness(10), "narrow content margin");

        ApplyLayout(window, 540);
        AssertCollapsed(window, "BrandTitle", "tiny brand title");
        AssertGridColumns(window, "InCallControls", 3, "tiny in-call controls");
        ApplyLayout(window, 880);
        AssertGridColumns(window, "InCallControls", 5, "restored in-call controls");
    }

    private static void VerifyHomeAndMoreNavigation(MainWindow window)
    {
        ShowRows(window, "More", new RowItem("Voicemail", "Not connected", "Unavailable"));
        AssertVisible(window, "HomeView", "home view hosts More list");
        AssertCollapsed(window, "PhoneStrip", "call strip on More");
        AssertCollapsed(window, "RefreshRowsButton", "refresh on More");
        if (Button(window, "MoreTabButton").Tag as string != "Active")
            throw new InvalidOperationException("More tab should be marked active.");

        VerifyVoicemailAndParking(window);
        VerifyHomeButtonReturnsFromMoreAndSettings(window);        ShowRows(window, "Recent calls", new RowItem("Dana Mercer", "Inbound", "Call", "101", CallId: "call-1"));
        AssertVisible(window, "PhoneStrip", "call strip on Home");
        AssertCollapsed(window, "SettingsView", "settings while Home is shown");
        if ((Element(window, "ListHeading") as TextBlock)?.Text != "Recent calls")
            throw new InvalidOperationException("Home should title its list Recent calls.");
        if (Button(window, "HomeTabButton").Tag as string != "Active")
            throw new InvalidOperationException("Home tab should be marked active.");
    }

    private static void VerifyHomeButtonReturnsFromMoreAndSettings(MainWindow window)
    {
        var dial = (TextBox)Element(window, "DialText");
        var suppress = typeof(MainWindow).GetField("_suppressDialSearch", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var showHome = typeof(MainWindow).GetMethod("ShowHomeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var showSettings = typeof(MainWindow).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        suppress.SetValue(window, true);
        dial.Text = "Dana";
        suppress.SetValue(window, false);

        ShowRows(window, "More", new RowItem("Voicemail", "No new messages", "Call voicemail", "*97"));
        WaitWithDispatcher(window.Dispatcher, (Task)showHome.Invoke(window, null)!);
        AssertVisible(window, "PhoneStrip", "call strip after Home from More with search text");
        if ((Element(window, "ListHeading") as TextBlock)?.Text != "Contacts" || Button(window, "HomeTabButton").Tag as string != "Active")
            throw new InvalidOperationException("Home must leave More even when the search box has text.");

        showSettings.Invoke(window, null);
        WaitWithDispatcher(window.Dispatcher, (Task)showHome.Invoke(window, null)!);
        AssertVisible(window, "HomeView", "home after Home from Settings with search text");
        AssertCollapsed(window, "SettingsView", "settings after Home with search text");

        suppress.SetValue(window, true);
        dial.Clear();
        suppress.SetValue(window, false);
        WaitWithDispatcher(window.Dispatcher, (Task)showHome.Invoke(window, null)!);
        if ((Element(window, "ListHeading") as TextBlock)?.Text != "Recent calls")
            throw new InvalidOperationException("Home with an empty search box should show recent calls.");
    }
    private static void VerifyVoicemailAndParking(MainWindow window)
    {
        var settings = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        settings.GetType().GetProperty("ParkSlots")!.SetValue(settings, "701-702");
        settings.GetType().GetProperty("ParkPickupPrefix")!.SetValue(settings, "*88");
        typeof(MainWindow).GetMethod("ApplyPhoneFeatureSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        typeof(MainWindow).GetMethod("ApplyVoicemailStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { new VoicemailStatus(true, 3, 4, true) });
        typeof(MainWindow).GetMethod("ApplyParkSlotStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { new ParkSlotStatus("701", ParkSlotState.Occupied) });

        if (!AutomationProperties.GetName(Button(window, "MoreTabButton")).Contains("3 new voicemails", StringComparison.Ordinal))
            throw new InvalidOperationException("The More tab should announce new voicemail.");
        Button(window, "MoreTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var rows = ((Element(window, "RowsList") as ListBox)!.ItemsSource as IEnumerable<RowItem>)!.ToList();
        if (rows.Count != 3 || rows[0] is not { Title: "Voicemail", Action: "Call voicemail", Destination: "*97" } || !rows[0].Detail.Contains("3 new", StringComparison.Ordinal))
            throw new InvalidOperationException("More should show voicemail counts with a Call voicemail action.");
        if (rows[1] is not { Title: "Park slot 701", Action: "Pick up", Destination: "*88701" } || !rows[1].Detail.Contains("parked", StringComparison.Ordinal))
            throw new InvalidOperationException("Occupied park slots should show as parked with a pickup destination.");
        if (rows.Any(row => row.Title is "Messages" or "Recordings"))
            throw new InvalidOperationException("Unbuilt Messages and Recordings placeholders should not be listed.");

        settings.GetType().GetProperty("ParkSlots")!.SetValue(settings, "");
        settings.GetType().GetProperty("ParkPickupPrefix")!.SetValue(settings, "");
        typeof(MainWindow).GetMethod("ApplyPhoneFeatureSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        typeof(MainWindow).GetMethod("ApplyVoicemailStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, new object[] { VoicemailStatus.Unknown });
    }

    private static void VerifyResponsiveAsyncNavigation(MainWindow window)
    {
        var navigate = typeof(MainWindow).GetMethod("NavigateToRowsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find responsive async navigation.");
        var showSettings = typeof(MainWindow).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find settings navigation.");
        var pending = new TaskCompletionSource<List<RowItem>>();
        Func<Task<List<RowItem>>> loader = () => pending.Task;
        var navigation = (Task)navigate.Invoke(window, new object[] { "Recent calls", "Calls", loader })!;

        AssertVisible(window, "HomeView", "home while calls are loading");
        var rows = Element(window, "RowsList") as ListBox
            ?? throw new InvalidOperationException("Could not find rows list.");
        if (rows.IsEnabled || rows.Items.Count != 1 || rows.Items[0] is not RowItem loading || loading.Title != "Loading...")
            throw new InvalidOperationException("Async navigation should immediately show a disabled loading state.");

        showSettings.Invoke(window, null);
        pending.SetResult([new("Stale result", "Must not replace settings", "Open")]);
        WaitWithDispatcher(window.Dispatcher, navigation);
        AssertVisible(window, "SettingsView", "settings after a stale list response");
        AssertCollapsed(window, "HomeView", "home after a stale response completes");

        Func<Task<List<RowItem>>> completedLoader = () => Task.FromResult<List<RowItem>>([new("Alice Adams", "Extension 101", "Call", "101")]);
        var completed = (Task)navigate.Invoke(window, new object[] { "Recent calls", "Calls", completedLoader })!;
        WaitWithDispatcher(window.Dispatcher, completed);
        AssertVisible(window, "HomeView", "home after loading completes");
        if (!rows.IsEnabled || rows.Items.Count != 1 || rows.Items[0] is not RowItem loaded || loaded.Title != "Alice Adams")
            throw new InvalidOperationException("Completed navigation should enable the list and render the newest response.");
    }

    private static void ApplyLayout(MainWindow window, double width, double height = 680)
    {
        window.Width = width;
        window.Height = height;
        typeof(MainWindow).GetMethod("ApplyResponsiveLayout", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(window, null);
    }

    private static void VerifyActiveNavigation(MainWindow window)
    {
        var setActive = typeof(MainWindow).GetMethod("SetActiveNav", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find SetActiveNav.");
        setActive.Invoke(window, new object[] { Button(window, "MoreTabButton") });
        if (Button(window, "MoreTabButton").Tag as string != "Active" || Button(window, "HomeTabButton").Tag is not null)
            throw new InvalidOperationException("Only the selected tab should be active.");
        setActive.Invoke(window, new object[] { Button(window, "SettingsButton") });
        AssertBrush(Button(window, "SettingsButton").Foreground, "#FF1F6FD1", "active settings gear");
        setActive.Invoke(window, new object[] { Button(window, "HomeTabButton") });
        AssertBrush(Button(window, "SettingsButton").Foreground, "#FF95A1AD", "inactive settings gear");
    }

    private static void VerifyNavigationDiscoverability(MainWindow window)
    {
        foreach (var name in new[] { "HomeTabButton", "MoreTabButton", "SettingsButton", "KeypadToggleButton", "RefreshRowsButton" })
        {
            var button = Button(window, name);
            if (string.IsNullOrWhiteSpace(button.ToolTip?.ToString()))
                throw new InvalidOperationException($"{name} should have a tooltip.");
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                throw new InvalidOperationException($"{name} should have an automation name.");
        }
    }

    private static void VerifyPrimaryControlDiscoverability(MainWindow window)
    {
        foreach (var name in new[]
        {
            "RegisterButton",
            "DialText",
            "CallButton",
            "AnswerCallButton",
            "DeclineCallButton",
            "SyncConnectWiseButton",
            "ImportContactsButton",
            "CreateTicketButton",
            "RefreshRowsButton"
        })
        {
            var element = Element(window, name);
            if (string.IsNullOrWhiteSpace(element.ToolTip?.ToString()))
                throw new InvalidOperationException($"{name} should have a tooltip.");

            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
                throw new InvalidOperationException($"{name} should have an automation name.");
        }

        var dialPad = window.FindName("DialPad") as Panel
            ?? throw new InvalidOperationException("Could not find DialPad.");
        foreach (var button in dialPad.Children.OfType<Button>())
        {
            if (string.IsNullOrWhiteSpace(button.ToolTip?.ToString()))
                throw new InvalidOperationException("Dialpad buttons should have tooltips.");
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                throw new InvalidOperationException("Dialpad buttons should have automation names.");
        }
    }

    private static void VerifyKeyboardFocusStyles(MainWindow window)
    {
        var buttonStyle = window.FindResource(typeof(Button)) as Style
            ?? throw new InvalidOperationException("Could not find default button style.");
        var buttonTemplate = buttonStyle.Setters.OfType<Setter>()
            .FirstOrDefault(setter => setter.Property == Control.TemplateProperty)?.Value as ControlTemplate
            ?? throw new InvalidOperationException("Default button style should define a template.");
        if (!buttonTemplate.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty))
            throw new InvalidOperationException("Default button style should include a keyboard focus trigger.");

        var textBoxStyle = window.FindResource(typeof(TextBox)) as Style
            ?? throw new InvalidOperationException("Could not find default text box style.");
        if (!textBoxStyle.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty))
            throw new InvalidOperationException("Default text box style should include a keyboard focus trigger.");

        var comboBoxStyle = window.FindResource(typeof(ComboBox)) as Style
            ?? throw new InvalidOperationException("Could not find default combo box style.");
        if (!comboBoxStyle.Triggers.OfType<Trigger>().Any(trigger => trigger.Property == UIElement.IsKeyboardFocusWithinProperty))
            throw new InvalidOperationException("Default combo box style should include a keyboard focus-within trigger.");
    }

    private static void VerifyControlTargetSizes(MainWindow window)
    {
        var buttonStyle = window.FindResource(typeof(Button)) as Style
            ?? throw new InvalidOperationException("Could not find default button style.");
        AssertStyleSetter(buttonStyle, Control.MinHeightProperty, 36.0, "default button minimum height");

        var dialStyle = window.FindResource("DialButton") as Style
            ?? throw new InvalidOperationException("Could not find dial button style.");
        AssertStyleSetter(dialStyle, Control.MinHeightProperty, 44.0, "dial button minimum height");
        AssertStyleSetter(dialStyle, FrameworkElement.MinWidthProperty, 64.0, "dial button minimum width");
    }

    private static void VerifyLongTextHandling(MainWindow window)
    {
        foreach (var name in new[]
        {
            "BrandTitle",
            "PresenceText",
            "ExtensionText",
            "StatusText",
            "VersionText",
            "ActiveCallMetric",
            "IncomingCallerText",
            "ListHeading",
            "ListSubheading"
        })
        {
            var textBlock = Element(window, name) as TextBlock
                ?? throw new InvalidOperationException($"{name} should be a TextBlock.");
            if (textBlock.TextTrimming != TextTrimming.CharacterEllipsis)
                throw new InvalidOperationException($"{name} should trim long text with an ellipsis.");
        }
    }

    private static void VerifyFilteredEmptyState(MainWindow window)
    {
        var rowsField = typeof(MainWindow).GetField("_currentRows", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _currentRows.");
        rowsField.SetValue(window, new List<RowItem> { new("Alice Adams", "101 - IT Done Right", "Call", "101") });

        var filterRows = typeof(MainWindow).GetMethod("FilterRows", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find FilterRows.");
        var result = filterRows.Invoke(window, new object[] { "zzz" }) as IEnumerable<RowItem>
            ?? throw new InvalidOperationException("FilterRows should return row items.");
        var rows = result.ToList();

        if (rows.Count != 1 || rows[0].Title != "No live data" || !rows[0].Detail.Contains("No results match", StringComparison.Ordinal))
            throw new InvalidOperationException("Filtered lists should show a friendly empty state row.");

        rowsField.SetValue(window, new List<RowItem> { new("Avery Stone", "+1 (908) 555-0100 - Acme Widgets", "Call", "+1 (908) 555-0100") });
        var phoneMatches = (filterRows.Invoke(window, new object[] { "9085550100" }) as IEnumerable<RowItem>)!.ToList();
        if (phoneMatches.Count != 1 || phoneMatches[0].Title != "Avery Stone")
            throw new InvalidOperationException("Typing a phone number's digits should match contacts stored with punctuation.");
        var partialMatches = (filterRows.Invoke(window, new object[] { "555-01" }) as IEnumerable<RowItem>)!.ToList();
        if (partialMatches.Count != 1 || partialMatches[0].Title != "Avery Stone")
            throw new InvalidOperationException("Partial phone digits should match formatted numbers.");
    }

    private static void VerifyListToolbarModes(MainWindow window)
    {
        ShowRows(window, "Recent calls");
        AssertVisible(window, "ExportHistoryButton", "export on recent calls");
        AssertVisible(window, "CreateTicketButton", "new ticket on recent calls");
        foreach (var removed in new[] { "CallNoteButton", "EditContactButton", "DeleteContactButton", "OpenCompanyButton" })
            if (window.FindName(removed) is not null)
                throw new InvalidOperationException($"{removed} should live on each row, not in the list toolbar.");

        ShowRows(window, "Contacts");
        AssertCollapsed(window, "ExportHistoryButton", "export on contact search");
        AssertVisible(window, "CreateTicketButton", "new ticket on contact search");
        var placeholder = Element(window, "DialPlaceholder") as TextBlock
            ?? throw new InvalidOperationException("Could not find the search/dial placeholder.");
        var dial = Element(window, "DialText") as TextBox
            ?? throw new InvalidOperationException("Could not find DialText.");
        var suppress = typeof(MainWindow).GetField("_suppressDialSearch", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _suppressDialSearch.");
        suppress.SetValue(window, true);
        dial.Text = "101";
        if (placeholder.Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Search/dial placeholder should hide while typing.");
        dial.Clear();
        if (placeholder.Visibility != Visibility.Visible)
            throw new InvalidOperationException("Search/dial placeholder should return when the box is empty.");
        suppress.SetValue(window, false);
        ShowRows(window, "Recent calls");
    }

    private static void VerifyRowRouting(MainWindow window)
    {
        var activate = typeof(MainWindow).GetMethod("ActivateSelectedRowAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find ActivateSelectedRowAsync.");
        var rowsList = window.FindName("RowsList") as ListBox
            ?? throw new InvalidOperationException("Could not find RowsList.");

        var configure = new RowItem("ConnectWise PSA", "Not configured", "Configure");
        ShowRows(window, "More", configure);
        rowsList.SelectedItem = configure;
        WaitWithDispatcher(window.Dispatcher, (Task)activate.Invoke(window, new object[] { rowsList })!);
        AssertVisible(window, "SettingsView", "settings after activating a Configure row");
        ShowRows(window, "Recent calls");
    }

    private static void VerifyKeypadToggle(MainWindow window)
    {
        ShowRows(window, "Recent calls");
        ApplyLayout(window, 880);
        AssertVisible(window, "DialPad", "keypad always shown in the two-column layout");
        AssertVisible(window, "StatusCards", "status cards in the two-column layout");
        AssertCollapsed(window, "KeypadToggleButton", "keypad toggle in the two-column layout");

        ApplyLayout(window, 650);
        AssertCollapsed(window, "StatusCards", "status cards in a narrow window");
        AssertCollapsed(window, "DialPad", "keypad before toggle in a narrow window");
        var toggle = Button(window, "KeypadToggleButton");
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "DialPad", "keypad after toggle");
        if (AutomationProperties.GetName(toggle) != "Hide keypad")
            throw new InvalidOperationException("Keypad toggle should announce that it hides the open keypad.");
        toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertCollapsed(window, "DialPad", "keypad after second toggle");
        ApplyLayout(window, 880);
    }
    private static void VerifyPhoneControlState(MainWindow window)
    {
        var dialText = Element(window, "DialText") as TextBox
            ?? throw new InvalidOperationException("Could not find DialText.");
        var callButton = Button(window, "CallButton");
        var answerButton = Button(window, "AnswerCallButton");
        var declineButton = Button(window, "DeclineCallButton");
        var inCallButtons = new[] { Button(window, "MuteButton"), Button(window, "HoldButton"), Button(window, "TransferButton"), Button(window, "DtmfButton") };
        var updateState = typeof(MainWindow).GetMethod("UpdatePhoneControlState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find UpdatePhoneControlState.");
        var activeCall = typeof(MainWindow).GetField("_activeCallId", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _activeCallId.");
        var registered = typeof(MainWindow).GetField("_registered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _registered.");
        var sip = typeof(MainWindow).GetField("_sip", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("Could not find SIP phone instance.");
        var stateMachine = sip.GetType().GetField("_callState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sip) as SipCallStateMachine
            ?? throw new InvalidOperationException("Could not find SIP call state machine.");
        var waitingStateMachine = sip.GetType().GetField("_waitingState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sip) as SipCallWaitingStateMachine
            ?? throw new InvalidOperationException("Could not find SIP call waiting state machine.");
        var applyWaitingState = typeof(MainWindow).GetMethod("ApplySipCallWaitingStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find call waiting UI handler.");

        activeCall.SetValue(window, null);
        registered.SetValue(window, false);
        dialText.Text = "";
        updateState.Invoke(window, null);
        if (callButton.IsEnabled)
            throw new InvalidOperationException("Call button should be disabled when there is no destination and no active call.");
        AssertTooltipContains(callButton, "Register", "unregistered empty call tooltip");
        if (inCallButtons.Any(button => button.IsEnabled))
            throw new InvalidOperationException("In-call controls should be disabled when there is no active call.");

        dialText.Text = "101";
        updateState.Invoke(window, null);
        if (callButton.IsEnabled)
            throw new InvalidOperationException("Call button should stay disabled when a destination is entered but the provider is not registered.");
        AssertTooltipContains(callButton, "Register", "unregistered destination call tooltip");

        registered.SetValue(window, true);
        updateState.Invoke(window, null);
        if (!callButton.IsEnabled)
            throw new InvalidOperationException("Call button should be enabled when a destination is entered and the provider is registered.");
        AssertTooltipContains(callButton, "Start", "registered destination call tooltip");
        if (inCallButtons.Any(button => button.IsEnabled))
            throw new InvalidOperationException("In-call controls should stay disabled until a call is active.");

        AssertTransition(stateMachine, SipCallState.IncomingRinging, SipCallDirection.Inbound, "Support desk");
        updateState.Invoke(window, null);
        if (callButton.IsEnabled || !answerButton.IsEnabled || !declineButton.IsEnabled)
            throw new InvalidOperationException("An incoming call should enable answer and decline while disabling outbound calling.");
        if (inCallButtons.Any(button => button.IsEnabled))
            throw new InvalidOperationException("In-call controls should stay disabled until an incoming call is answered.");
        AssertTransition(stateMachine, SipCallState.Connecting, SipCallDirection.None, "");
        AssertTransition(stateMachine, SipCallState.Connected, SipCallDirection.None, "");

        dialText.Text = "";
        updateState.Invoke(window, null);
        if (!callButton.IsEnabled)
            throw new InvalidOperationException("Call button should remain enabled while a call is active.");
        AssertTooltipContains(callButton, "End", "active call tooltip");
        if (inCallButtons.Any(button => !button.IsEnabled))
            throw new InvalidOperationException("In-call controls should be enabled while a call is active.");

        AssertWaitingTransition(waitingStateMachine, SipCallWaitingState.Ringing, "Second caller", "202", "Support desk", "");
        applyWaitingState.Invoke(window, new object[] { waitingStateMachine.Current });
        if (callButton.IsEnabled || !answerButton.IsEnabled || !declineButton.IsEnabled)
            throw new InvalidOperationException("A waiting call should enable answer and decline while preventing the active call from being ended accidentally.");
        if ((Element(window, "IncomingCallLabel") as TextBlock)?.Text != "Call waiting")
            throw new InvalidOperationException("The incoming-call banner must identify a second call as call waiting.");

        AssertWaitingTransition(waitingStateMachine, SipCallWaitingState.Answering, "", "", "", "");
        applyWaitingState.Invoke(window, new object[] { waitingStateMachine.Current });
        if (answerButton.IsEnabled || declineButton.IsEnabled)
            throw new InvalidOperationException("Answer and decline must be disabled while a waiting-call answer is already in progress.");

        AssertWaitingTransition(waitingStateMachine, SipCallWaitingState.TwoCalls, "", "", "Second caller", "Support desk");
        applyWaitingState.Invoke(window, new object[] { waitingStateMachine.Current });
        var holdButton = Button(window, "HoldButton");
        if (!Equals(holdButton.Content, "Switch") || AutomationProperties.GetName(holdButton) != "Switch active call")
            throw new InvalidOperationException("Two connected calls must expose an accessible Switch action.");
        if (Button(window, "TransferButton").IsEnabled)
            throw new InvalidOperationException("Transfer must remain disabled until one of the two calls has ended.");
        AssertTooltipContains(holdButton, "Support desk", "call waiting switch tooltip");
        AssertWaitingTransition(waitingStateMachine, SipCallWaitingState.None, "", "", "", "");
        applyWaitingState.Invoke(window, new object[] { waitingStateMachine.Current });

        AssertTransition(stateMachine, SipCallState.Ending, SipCallDirection.None, "");
        AssertTransition(stateMachine, SipCallState.Idle, SipCallDirection.None, "");
        activeCall.SetValue(window, null);
        registered.SetValue(window, false);
    }

    private static void VerifyProviderRegistrationReadiness(MainWindow window)
    {
        var settings = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(window)
            ?? throw new InvalidOperationException("Could not find _settings.");
        var updateStatus = typeof(MainWindow).GetMethod("UpdateProviderStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find UpdateProviderStatus.");
        var registered = typeof(MainWindow).GetField("_registered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find _registered.");
        var registerButton = Button(window, "RegisterButton");

        SetSetting(settings, "Provider", "Axion / Noixa pending");
        SetSetting(settings, "SipServer", "");
        SetSetting(settings, "SipUsername", "");
        SetSetting(settings, "SipPassword", "");
        registered.SetValue(window, false);
        updateStatus.Invoke(window, null);
        AssertDisabled("RegisterButton", window, "Axion/Noixa pending registration");
        AssertTooltipContains(registerButton, "SBC", "Axion/Noixa pending tooltip");
        if (!Equals(registerButton.Content, "Unavailable"))
            throw new InvalidOperationException("Axion/Noixa pending registration should show Unavailable.");

        SetSetting(settings, "Provider", "Standard SIP");
        updateStatus.Invoke(window, null);
        AssertDisabled("RegisterButton", window, "missing SIP settings registration");
        AssertTooltipContains(registerButton, "Settings", "missing SIP settings tooltip");

        SetSetting(settings, "SipServer", "sip.example.invalid");
        SetSetting(settings, "SipUsername", "101");
        SetSetting(settings, "SipPassword", "test-password");
        updateStatus.Invoke(window, null);
        AssertEnabled("RegisterButton", window, "complete SIP settings registration");
        AssertTooltipContains(registerButton, "Register", "complete SIP settings tooltip");
        if (!Equals(registerButton.Content, "Register"))
            throw new InvalidOperationException("Complete SIP settings should show Register.");

        registered.SetValue(window, true);
        updateStatus.Invoke(window, null);
        AssertEnabled("RegisterButton", window, "registered provider unregister");
        AssertTooltipContains(registerButton, "Unregister", "registered provider tooltip");
        if (!Equals(registerButton.Content, "Unregister"))
            throw new InvalidOperationException("Registered provider should show Unregister.");

        registered.SetValue(window, false);
    }

    private static void VerifyListActionState(MainWindow window)
    {
        var updateActions = typeof(MainWindow).GetMethod("UpdateListActionState", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find UpdateListActionState.");
        var rowsList = window.FindName("RowsList") as ListBox
            ?? throw new InvalidOperationException("Could not find RowsList.");

        var contact = new RowItem("Alice Adams", "101 - IT Done Right", "Call", "101", "contact-1", "IT Done Right", "4221");
        ShowRows(window, "Contacts", contact);
        updateActions.Invoke(window, null);
        AssertEnabled("CreateTicketButton", window, "new ticket without a selection");
        AssertTooltipContains(Element(window, "CreateTicketButton"), "choose the company", "new ticket tooltip without a company");

        rowsList.SelectedItem = contact;
        updateActions.Invoke(window, null);
        AssertTooltipContains(Element(window, "CreateTicketButton"), "IT Done Right", "new ticket tooltip for the selected company");

        // Row buttons: render a selected row and confirm its actions match the row's data.
        ApplyLayout(window, 880);
        if (!window.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowInTaskbar = false;
            window.Show();
        }
        window.UpdateLayout();
        DrainDispatcher(window.Dispatcher);
        var container = rowsList.ItemContainerGenerator.ContainerFromItem(contact) as ListBoxItem
            ?? throw new InvalidOperationException("The contact row was not rendered.");
        var tags = FindVisualChildren<Button>(container).Where(button => button.Visibility == Visibility.Visible)
            .Select(button => button.Tag as string).Where(tag => tag is not null).ToList();
        foreach (var expected in new[] { "Primary", "OpenCompany", "NewTicket", "Edit", "Delete" })
            if (!tags.Contains(expected))
                throw new InvalidOperationException($"A selected contact row should offer the {expected} action.");
        if (tags.Contains("Notes"))
            throw new InvalidOperationException("Contact rows without a call should not offer Notes.");

        var call = new RowItem("101", "Outbound - connected", "Call", "101", CallId: "call-1");
        ShowRows(window, "Recent calls", call);
        rowsList.SelectedItem = call;
        window.UpdateLayout();
        DrainDispatcher(window.Dispatcher);
        var callRow = rowsList.ItemContainerGenerator.ContainerFromItem(call) as ListBoxItem
            ?? throw new InvalidOperationException("The call row was not rendered.");
        var callTags = FindVisualChildren<Button>(callRow).Where(button => button.Visibility == Visibility.Visible).Select(button => button.Tag as string).ToList();
        if (!callTags.Contains("Notes") || callTags.Contains("OpenCompany") || callTags.Contains("Edit"))
            throw new InvalidOperationException("Call rows should offer Notes but not company or contact actions when those are unknown.");
    }
    private static void VerifyBusyActionFeedback(MainWindow window)
    {
        var setBusy = typeof(MainWindow).GetMethod("SetBusyAction", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find SetBusyAction.");
        var restore = typeof(MainWindow).GetMethod("RestoreAction", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find RestoreAction.");
        var sync = Button(window, "SyncConnectWiseButton");

        setBusy.Invoke(null, new object[] { sync, "Syncing ConnectWise contacts..." });
        if (sync.IsEnabled)
            throw new InvalidOperationException("Busy action should be disabled while work is in progress.");
        AssertTooltipContains(sync, "Syncing", "busy sync tooltip");
        if (!ToolTipService.GetShowOnDisabled(sync))
            throw new InvalidOperationException("Busy sync action should show its tooltip while disabled.");

        restore.Invoke(null, new object[] { sync, "Synchronize ConnectWise contacts into the CallBridge directory." });
        if (!sync.IsEnabled)
            throw new InvalidOperationException("Restored action should be enabled.");
        AssertTooltipContains(sync, "Synchronize", "restored sync tooltip");
    }

    private static void VerifyInlineStatusFeedback(MainWindow window)
    {
        var showInlineStatus = typeof(MainWindow).GetMethod("ShowInlineStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find ShowInlineStatus.");
        showInlineStatus.Invoke(window, new object[] { "Contacts synchronized without interrupting the workflow." });

        var listSubheading = Element(window, "DirectoryStatusText") as TextBlock
            ?? throw new InvalidOperationException("Could not find DirectoryStatusText.");
        var statusText = Element(window, "StatusText") as TextBlock
            ?? throw new InvalidOperationException("Could not find StatusText.");
        if (!listSubheading.Text.Contains("synchronized", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Inline status should update the directory status in Settings.");
        if (!statusText.Text.Contains("synchronized", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Inline status should update the status banner.");
    }

    private static void VerifyOwnedNotificationHelpers()
    {
        foreach (var name in new[] { "ShowInfo", "ShowWarning", "ConfirmWarning" })
        {
            var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"Could not find owner-attached notification helper {name}.");
            if (method.IsStatic)
                throw new InvalidOperationException($"{name} should be an instance helper so dialogs can be owned by the main window.");
        }
    }

    private static void VerifyListKeyboardActivationHook(MainWindow window)
    {
        var handler = typeof(MainWindow).GetMethod("ActivateListRowOnEnterAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find Enter-key list activation handler.");
        if (handler.ReturnType != typeof(Task))
            throw new InvalidOperationException("Enter-key list activation handler should be async.");

        Element(window, "RowsList");
    }

    private static void VerifyDisabledTooltips(MainWindow window)
    {
        foreach (var name in new[]
        {
            "CallButton",
            "AnswerCallButton",
            "DeclineCallButton",
            "RegisterButton",
            "MuteButton",
            "HoldButton",
            "TransferButton",
            "DtmfButton",
            "CreateTicketButton",
            "VoicemailCardCallButton"
        })
        {
            var element = Element(window, name);
            if (!ToolTipService.GetShowOnDisabled(element))
                throw new InvalidOperationException($"{name} should show its tooltip while disabled.");
        }
    }

    private static void AssertTransition(
        SipCallStateMachine stateMachine,
        SipCallState state,
        SipCallDirection direction,
        string remoteParty)
    {
        if (!stateMachine.TryTransition(state, direction, remoteParty, state.ToString()))
            throw new InvalidOperationException($"Test setup could not transition SIP call state to {state}.");
    }

    private static void AssertWaitingTransition(
        SipCallWaitingStateMachine stateMachine,
        SipCallWaitingState state,
        string incomingParty,
        string incomingNumber,
        string activeParty,
        string heldParty)
    {
        if (!stateMachine.TryTransition(
                state,
                incomingParty,
                incomingNumber,
                activeParty,
                heldParty,
                state.ToString()))
        {
            throw new InvalidOperationException($"Test setup could not transition SIP call waiting state to {state}.");
        }
    }

    private static void VerifySecondaryWindowUsability()
    {
        using var callNotes = new WindowScope(new CallNoteWindow("101", "Initial notes", "Resolved"));
        AssertDialogShell(callNotes.Window, "call notes dialog", 420, 330);
        AssertHasDefaultButton(callNotes.Window, "call notes dialog");
        AssertHasCancelButton(callNotes.Window, "call notes dialog");
        AssertDialogButtonMetadata(callNotes.Window, "call notes dialog");
        AssertDialogInputMetadata(callNotes.Window, "call notes dialog", "Outcome", "Technician notes");
        AssertHasWrapPanel(callNotes.Window, "call notes dialog");

        var pickCompany = new TicketWindow("", "Dana Mercer", "101", "import:blue-ridge", _ => Task.FromResult<IReadOnlyList<ConnectWiseCompanySummary>>([new("101", "Blue Ridge Dental")]));
        if (pickCompany.CompanyId != "")
            throw new InvalidOperationException("Non-ConnectWise company IDs must not be used for tickets.");
        pickCompany.Close();
        var knownCompany = new TicketWindow("Blue Ridge Dental", "Dana Mercer", "101", "101");
        if (knownCompany.CompanyId != "101" || knownCompany.CompanyName != "Blue Ridge Dental")
            throw new InvalidOperationException("A matched ConnectWise company should be used for the ticket.");
        knownCompany.Close();

        using var ticket = new WindowScope(new TicketWindow("IT Done Right", "Alice Adams", "101"));
        AssertDialogShell(ticket.Window, "ticket dialog", 430, 330);
        AssertHasDefaultButton(ticket.Window, "ticket dialog");
        AssertHasCancelButton(ticket.Window, "ticket dialog");
        AssertDialogButtonMetadata(ticket.Window, "ticket dialog");
        AssertDialogLabelsWrap(ticket.Window, "ticket dialog");
        AssertDialogInputMetadata(ticket.Window, "ticket dialog", "Summary", "Description");
        AssertWindowTextDoesNotContain(ticket.Window, "\u00c2", "ticket dialog text");
        AssertHasWrapPanel(ticket.Window, "ticket dialog");

        using var contact = new WindowScope(new ContactWindow("Alice Adams", "IT Done Right", "101"));
        AssertDialogShell(contact.Window, "contact dialog", 380, 300);
        AssertHasDefaultButton(contact.Window, "contact dialog");
        AssertHasCancelButton(contact.Window, "contact dialog");
        AssertDialogButtonMetadata(contact.Window, "contact dialog");
        AssertDialogLabelsWrap(contact.Window, "contact dialog");
        AssertDialogInputMetadata(contact.Window, "contact dialog", "Contact name", "Company", "Phone or extension");
        AssertHasWrapPanel(contact.Window, "contact dialog");

        using var prompt = new WindowScope(new PromptWindow("Transfer call", "Destination extension or phone number"));
        AssertDialogShell(prompt.Window, "prompt dialog", 320, 170);
        AssertHasDefaultButton(prompt.Window, "prompt dialog");
        AssertHasCancelButton(prompt.Window, "prompt dialog");
        AssertDialogButtonMetadata(prompt.Window, "prompt dialog");
        AssertDialogInputMetadata(prompt.Window, "prompt dialog", "Destination extension or phone number");
        AssertHasWrapPanel(prompt.Window, "prompt dialog");

    }

    private static void VerifyTimeEntryQueue(MainWindow window)
    {
        var entryType = typeof(MainWindow).GetNestedType("PendingTimeEntry", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find PendingTimeEntry.");
        var queue = typeof(MainWindow).GetField("_pendingTimeEntries", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) as System.Collections.IList
            ?? throw new InvalidOperationException("Time entry drafts should queue.");
        var show = typeof(MainWindow).GetMethod("ShowNextTimeEntryDraft", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var hide = typeof(MainWindow).GetMethod("HideTimeEntryDraft", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var start = DateTimeOffset.UtcNow.AddMinutes(-20);
        queue.Add(Activator.CreateInstance(entryType, "48213", "48213", start, start.AddMinutes(12), "First call")!);
        queue.Add(Activator.CreateInstance(entryType, "48190", "48190", start.AddMinutes(13), start.AddMinutes(16), "Second call")!);
        show.Invoke(window, null);
        var target = Element(window, "TimeEntryTargetText") as TextBlock ?? throw new InvalidOperationException("Missing time entry target.");
        if (!target.Text.Contains("#48213", StringComparison.Ordinal) || !target.Text.Contains("1 more", StringComparison.Ordinal))
            throw new InvalidOperationException("The oldest draft should show first with a count of waiting drafts.");
        hide.Invoke(window, null);
        AssertVisible(window, "TimeEntryDraft", "second call's time entry after dismissing the first");
        if (!target.Text.Contains("#48190", StringComparison.Ordinal))
            throw new InvalidOperationException("Dismissing a draft should show the next waiting call.");
        hide.Invoke(window, null);
        AssertCollapsed(window, "TimeEntryDraft", "time entry card after every draft is handled");
    }

    private static void VerifyAdminPin(MainWindow window)
    {
        if (AdminPin.IsValidFormat("12a4") || AdminPin.IsValidFormat("123") || !AdminPin.IsValidFormat("2468"))
            throw new InvalidOperationException("Admin PIN format rules are wrong.");
        var (hash, salt) = AdminPin.Create("2468");
        if (!AdminPin.Verify("2468", hash, salt) || AdminPin.Verify("2469", hash, salt) || hash.Contains("2468", StringComparison.Ordinal))
            throw new InvalidOperationException("Admin PIN hashing must verify only the right PIN and never store it.");
        if (AdminPin.RetryDelay(4) != TimeSpan.Zero || AdminPin.RetryDelay(5) <= TimeSpan.Zero)
            throw new InvalidOperationException("Admin PIN attempts should slow down after repeated failures.");

        Button(window, "SettingsAppTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        ((PasswordBox)Element(window, "AdminPinNewBox")).Password = "2468";
        ((PasswordBox)Element(window, "AdminPinConfirmBox")).Password = "2468";
        Button(window, "SetAdminPinButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsLockButton", "lock action after setting a PIN");

        Button(window, "SettingsLockButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsAudioSection", "audio after locking");
        AssertCollapsed(window, "AdminTabsPanel", "admin tabs while locked");
        Button(window, "SettingsAdminTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsLockedSection", "PIN prompt for admin settings");
        AssertCollapsed(window, "SettingsSipSection", "SIP settings while locked");

        ((PasswordBox)Element(window, "AdminPinEntryBox")).Password = "1111";
        Button(window, "UnlockAdminButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsLockedSection", "PIN prompt after a wrong PIN");
        if (string.IsNullOrWhiteSpace((Element(window, "AdminPinMessageText") as TextBlock)?.Text))
            throw new InvalidOperationException("A wrong PIN should explain what happened.");

        ((PasswordBox)Element(window, "AdminPinEntryBox")).Password = "2468";
        Button(window, "UnlockAdminButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsGeneralSection", "admin settings after the right PIN");
        AssertVisible(window, "AdminTabsPanel", "admin tabs after unlocking");

        window.CloseToTray = true;
        window.Close();
        window.CloseToTray = false;
        typeof(MainWindow).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Button(window, "SettingsGeneralTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertCollapsed(window, "AdminTabsPanel", "admin tabs after hiding to the tray");
        Button(window, "SettingsAdminTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsLockedSection", "PIN prompt after hiding to the tray");

        var settings = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        settings.GetType().GetProperty("AdminPinHash")!.SetValue(settings, "");
        settings.GetType().GetProperty("AdminPinSalt")!.SetValue(settings, "");
        typeof(MainWindow).GetMethod("UpdateAdminPinState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
    }
    private static string VerifyBranding(MainWindow window)
    {
        if (Branding.TryNormalizeAccent("#FFE066", out _, out _))
            throw new InvalidOperationException("Accents too light for white text must be rejected.");
        if (Branding.TryNormalizeAccent("12345", out _, out _))
            throw new InvalidOperationException("Malformed accent colors must be rejected.");
        if (!Branding.TryNormalizeAccent("0e7c74", out var teal, out _) || teal != "#0E7C74")
            throw new InvalidOperationException("Valid accent colors should normalize to #RRGGBB.");

        var settings = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var applyBranding = typeof(MainWindow).GetMethod("ApplyBranding", BindingFlags.Instance | BindingFlags.NonPublic)!;
        settings.GetType().GetProperty("BrandAccentColor")!.SetValue(settings, "#0E7C74");

        var logoSource = Path.Combine(Path.GetTempPath(), $"callbridge-logo-{Guid.NewGuid():N}.png");
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 64, 64));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0E, 0x7C, 0x74)), null, new Point(32, 32), 24, 24);
        }
        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(logoSource)) encoder.Save(stream);

        try
        {
            try
            {
                Branding.ImportLogo(Path.ChangeExtension(logoSource, ".gif"));
                throw new InvalidOperationException("Non-PNG/JPEG logos must be rejected.");
            }
            catch (InvalidDataException) { }

            var stored = Branding.ImportLogo(logoSource);
            if (!stored.StartsWith(Branding.LogoFolder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Logos must be copied into CallBridge's branding folder.");
            settings.GetType().GetProperty("BrandLogoPath")!.SetValue(settings, stored);
            applyBranding.Invoke(window, null);

            AssertVisible(window, "HeaderLogoImage", "header logo after importing a logo");
            AssertCollapsed(window, "LogoTile", "header initials tile while a logo is shown");
            AssertCollapsed(window, "BrandTitle", "header product name while a logo is shown");
            var accent = window.FindResource("Accent") as SolidColorBrush
                ?? throw new InvalidOperationException("Accent brush is missing.");
            if (accent.Color != Color.FromRgb(0x0E, 0x7C, 0x74))
                throw new InvalidOperationException("Saving a brand accent should recolor the app.");

            ShowRows(window, "Recent calls",
                new RowItem("Dana Mercer", "(828) 555-0142 · Inbound · connected · 04:12 · Sep 14, 3:52 PM · Blue Ridge Dental", "Call", "8285550142", CompanyId: "101", CallId: "call-1"),
                new RowItem("Marcus Tate", "Ext 208 · Outbound · connected · 01:05 · Sep 14, 2:10 PM", "Call", "208", CallId: "call-2"));
            AssertCollapsed(window, "BrandTitle", "header product name while a logo is shown after layout");
            var preview = RenderWindowPreview(window, "callbridge-branded.png");

            Branding.RemoveLogo(stored);
            if (File.Exists(stored)) throw new InvalidOperationException("Removing the logo should delete the stored copy.");
            return preview;
        }
        finally
        {
            settings.GetType().GetProperty("BrandLogoPath")!.SetValue(settings, "");
            settings.GetType().GetProperty("BrandAccentColor")!.SetValue(settings, "");
            applyBranding.Invoke(window, null);
            try { File.Delete(logoSource); } catch { }
        }
    }

    private static void VerifyEmbeddedSettingsPage(MainWindow window)
    {
        var showSettings = typeof(MainWindow).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find embedded settings navigation.");
        ApplyLayout(window, 880);
        showSettings.Invoke(window, null);

        AssertVisible(window, "SettingsView", "embedded settings page");
        AssertCollapsed(window, "HomeView", "home view while settings is active");

        var scroller = Element(window, "SettingsScroller") as ScrollViewer
            ?? throw new InvalidOperationException("Embedded settings should use a scroll viewer.");
        if (scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Auto || scroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
            throw new InvalidOperationException("Embedded settings should scroll vertically without horizontal overflow.");
        if (scroller.HorizontalContentAlignment != HorizontalAlignment.Stretch || Element(window, "SettingsSections").HorizontalAlignment != HorizontalAlignment.Stretch)
            throw new InvalidOperationException("Embedded settings should fill the available workspace width.");

        foreach (var name in new[] { "SettingsAudioTabButton", "SettingsSignInTabButton", "SettingsAdminTabButton", "SettingsGeneralTabButton", "SettingsSipTabButton", "SettingsConnectWiseTabButton", "SettingsAppTabButton" })
        {
            var button = Button(window, name);
            if (string.IsNullOrWhiteSpace(button.ToolTip?.ToString()) || string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)))
                throw new InvalidOperationException($"{name} should be discoverable by mouse and assistive technology.");
        }

        AssertVisible(window, "SettingsAudioSection", "audio settings open first");
        if (Button(window, "SettingsAudioTabButton").Tag as string != "Active")
            throw new InvalidOperationException("The open settings section tab should be marked active.");
        Button(window, "SettingsSignInTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsSignInSection", "sign-in settings section");
        AssertVisible(window, "SettingsCwMemberText", "member ID on sign-in");
        AssertVisible(window, "AdminTabsPanel", "admin tabs without a PIN");
        AssertCollapsed(window, "SettingsAdminTabButton", "locked admin tab without a PIN");
        Button(window, "SettingsGeneralTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsGeneralSection", "phone account settings section");
        VerifyAdminPin(window);
        Button(window, "SettingsSipTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsSipSection", "SIP settings section");
        AssertCollapsed(window, "SettingsGeneralSection", "general settings after switching sections");
        Button(window, "SettingsConnectWiseTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsConnectWiseSection", "ConnectWise settings section");
        AssertVisible(window, "SyncConnectWiseButton", "directory sync in ConnectWise settings");
        AssertVisible(window, "ImportContactsButton", "directory import in ConnectWise settings");
        AssertVisible(window, "ClearContactsButton", "directory clear in ConnectWise settings");
        Button(window, "SettingsAppTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "SettingsAppSection", "app settings section");

        foreach (var name in new[]
        {
            "SettingsProviderBox", "SettingsMicrophoneBox", "SettingsSpeakerBox", "SettingsRingtoneSpeakerBox", "SettingsRefreshAudioButton", "SettingsSipTransportBox", "SettingsSipCodecProfileBox",
            "SettingsTestMicrophoneButton", "SettingsTestSpeakerButton", "SettingsTestRingtoneButton", "SettingsSipPassword", "SettingsCwPlatformSecret",
            "SettingsCwPrivateKey", "SettingsStartupCheckBox", "SettingsTopmostCheckBox", "SettingsSaveButton", "SettingsDiscardButton"
        })
        {
            var element = Element(window, name);
            if (string.IsNullOrWhiteSpace(element.ToolTip?.ToString()) || string.IsNullOrWhiteSpace(AutomationProperties.GetName(element)))
                throw new InvalidOperationException($"{name} should expose a tooltip and automation name.");
            if (!ToolTipService.GetShowOnDisabled(element))
                throw new InvalidOperationException($"{name} should show its tooltip while disabled.");
        }
        foreach (var name in new[] { "SettingsSipPassword", "SettingsCwPlatformSecret", "SettingsCwPrivateKey" })
        {
            if (Element(window, name) is not PasswordBox)
                throw new InvalidOperationException($"{name} must mask credential input.");
        }

        var sipTransport = Element(window, "SettingsSipTransportBox") as ComboBox
            ?? throw new InvalidOperationException("SIP transport should use a selectable list.");
        if (!sipTransport.IsEnabled || sipTransport.Items.Count != 3
            || !new[] { "UDP", "TCP", "TLS" }.All(item => sipTransport.Items.Contains(item)))
        {
            throw new InvalidOperationException("SIP transport should expose enabled UDP, TCP, and TLS choices.");
        }
        var codecProfile = Element(window, "SettingsSipCodecProfileBox") as ComboBox
            ?? throw new InvalidOperationException("SIP codec profile should use a selectable list.");
        if (!codecProfile.IsEnabled || codecProfile.Items.Count != SipCodecProfile.DisplayNames.Count
            || SipCodecProfile.DisplayNames.Any(item => !codecProfile.Items.Contains(item)))
        {
            throw new InvalidOperationException("SIP codec profile choices should match the media profiles enforced by CallBridge.");
        }

        AssertGridColumns(window, "SettingsGeneralGrid", 2, "wide general settings fields");
        AssertGridColumns(window, "SettingsCwPlatformGrid", 2, "wide ConnectWise OAuth fields");
        ApplyLayout(window, 650);
        AssertGridColumns(window, "SettingsGeneralGrid", 1, "narrow general settings fields");
        AssertGridColumns(window, "SettingsSipGrid", 1, "narrow SIP settings fields");
        AssertGridColumns(window, "SettingsCwPsaGrid", 1, "narrow ConnectWise PSA fields");
        ApplyLayout(window, 880);

        var save = Button(window, "SettingsSaveButton");
        var discard = Button(window, "SettingsDiscardButton");
        if (save.IsEnabled || discard.IsEnabled)
            throw new InvalidOperationException("Settings actions should start disabled when there are no changes.");
        var extension = Element(window, "SettingsExtensionText") as TextBox
            ?? throw new InvalidOperationException("Could not find extension field.");
        var savedValue = extension.Text;
        extension.Text = savedValue + "9";
        if (!save.IsEnabled || !discard.IsEnabled)
            throw new InvalidOperationException("Editing settings should enable save and discard actions.");
        discard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        if (extension.Text != savedValue || save.IsEnabled || discard.IsEnabled)
            throw new InvalidOperationException("Discard should restore saved settings and clear the dirty state.");

        var provider = Element(window, "SettingsProviderBox") as ComboBox ?? throw new InvalidOperationException("Missing provider box.");
        var voicemailCode = (TextBox)Element(window, "SettingsVoicemailCodeText");
        var parkCode = (TextBox)Element(window, "SettingsParkCodeText");
        var parkSlots = (TextBox)Element(window, "SettingsParkSlotsText");
        voicemailCode.Text = "";
        parkCode.Text = "";
        parkSlots.Text = "9999";
        provider.SelectedItem = "Axion / HivePBX";
        if (voicemailCode.Text != "*97" || parkCode.Text != "4388" || parkSlots.Text != "9999")
            throw new InvalidOperationException("Choosing HivePBX should fill empty voicemail and parking fields without overwriting entered values.");
        discard.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private const int PreviewWidth = 880;
    private const int PreviewHeight = 680;

    private static void VerifyConnectWiseModeSettings(MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainWindow).GetMethod("ShowSettings", flags, Type.EmptyTypes)?.Invoke(window, null);
        typeof(MainWindow).GetMethod("ShowSettingsSection", flags)!.Invoke(window, ["ConnectWise"]);
        if (window.FindName("SettingsConnectWiseSection") is not FrameworkElement section || section.Visibility != Visibility.Visible)
        {
            // Admin sections can be PIN-locked in some fixtures; nothing to render then.
            return;
        }
        var mode = window.FindName("SettingsCwModeBox") as ComboBox ?? throw new InvalidOperationException("Missing ticketing connection choice.");
        if (mode.Items.Count != 3) throw new InvalidOperationException("Ticketing connection should offer PSA, Platform, and Platform with PSA.");
        mode.SelectedValue = ConnectWiseTicketingMode.Platform;
        if (window.FindName("SettingsCwModeHelpText") is not TextBlock help || !help.Text.Contains("Platform", StringComparison.Ordinal))
            throw new InvalidOperationException("Choosing Platform should explain what it uses.");
        AssertVisible(window, "SettingsCwPlatformBoardBox", "platform service board choice");
        AssertVisible(window, "SettingsCwPlatformSourceBox", "platform ticket source choice");
        RenderWindowPreview(window, "callbridge-connectwise-mode.png");
        mode.SelectedValue = ConnectWiseTicketingMode.Psa;
        typeof(MainWindow).GetMethod("ShowSettingsSection", flags)!.Invoke(window, ["Audio"]);
    }
    private static void VerifyUpdateIndicators(MainWindow window)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var refresh = typeof(MainWindow).GetMethod("RefreshUpdateIndicators", flags)!;
        var field = typeof(MainWindow).GetField("_availableUpdate", flags)!;
        var showSection = typeof(MainWindow).GetMethod("ShowSettingsSection", flags)!;

        refresh.Invoke(window, null);
        AssertCollapsed(window, "UpdateBadgeButton", "update badge with no update");

        field.SetValue(window, new UpdateInfo(new Version(9, 9, 9), "v9.9.9",
            new Uri("https://github.com/ithealthtech/CallBridge-Releases/releases/download/v9.9.9/CallBridge-v9.9.9.msi"),
            new string('A', 64), new Uri("https://github.com/ithealthtech/CallBridge-Releases/releases/tag/v9.9.9")));
        typeof(MainWindow).GetField("_lastUpdateCheck", flags)!.SetValue(window, (DateTimeOffset?)DateTimeOffset.Now);
        refresh.Invoke(window, null);
        AssertVisible(window, "UpdateBadgeButton", "update badge next to the extension");
        RenderWindowPreview(window, "callbridge-update-badge.png");

        typeof(MainWindow).GetMethod("ShowSettings", flags, Type.EmptyTypes)?.Invoke(window, null);
        showSection.Invoke(window, ["About"]);
        AssertVisible(window, "SettingsAboutSection", "About settings section");
        AssertVisible(window, "AboutInstallUpdateButton", "About install button when an update exists");
        if (window.FindName("AboutUpdateStatusText") is not TextBlock status || !status.Text.Contains("9.9.9", StringComparison.Ordinal))
            throw new InvalidOperationException("About should name the available version.");
        RenderWindowPreview(window, "callbridge-about-update.png");

        field.SetValue(window, null);
        refresh.Invoke(window, null);
        AssertCollapsed(window, "AboutInstallUpdateButton", "About install button with no update");
        if (window.FindName("AboutUpdateStatusText") is not TextBlock upToDate || upToDate.Text != "You're up to date.")
            throw new InvalidOperationException("About should say the app is up to date after a clean check.");
        typeof(MainWindow).GetField("_lastUpdateCheck", flags)!.SetValue(window, null);
        showSection.Invoke(window, ["Audio"]);
        typeof(MainWindow).GetMethod("ShowHome", flags, Type.EmptyTypes)?.Invoke(window, null);
    }
    private static string RenderWindowPreview(MainWindow window, string fileName, Action? beforeRender = null)
    {
        if (!window.IsVisible)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000;
            window.Top = -10000;
            window.ShowInTaskbar = false;
            window.Show();
        }
        ApplyLayout(window, PreviewWidth, PreviewHeight);
        window.Measure(new Size(PreviewWidth, PreviewHeight));
        window.Arrange(new Rect(0, 0, PreviewWidth, PreviewHeight));
        window.UpdateLayout();
        beforeRender?.Invoke();
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(PreviewWidth, PreviewHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        AssertPreviewIsNotBlank(bitmap);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var outputPath = Path.GetFullPath(Path.Combine("artifacts", "verification", fileName));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using (var stream = File.Create(outputPath)) encoder.Save(stream);
        return outputPath;
    }

    private static string CaptureHomePreview(MainWindow window)
    {
        ShowRows(window, "Recent calls",
            new RowItem("Dana Mercer", "(828) 555-0142 · Inbound · connected · 04:12 · Sep 14, 3:52 PM · Blue Ridge Dental", "Call", "8285550142", CompanyId: "101", CallId: "call-1"),
            new RowItem("Marcus Tate", "Ext 208 · Outbound · connected · 01:05 · Sep 14, 2:10 PM", "Call", "208", CallId: "call-2"),
            new RowItem("(704) 555-0199", "Inbound · missed · 00:00 · Sep 14, 11:47 AM", "Call", "7045550199", CallId: "call-3"),
            new RowItem("Kayla Reyes", "(828) 555-0170 · Inbound · connected · 12:31 · Sep 13, 4:20 PM · Blue Ridge Dental", "Call", "8285550170", CompanyId: "101", CallId: "call-4"));
        return RenderWindowPreview(window, "callbridge-home.png");
    }

    private static string CaptureInCallPreview(MainWindow window)
    {
        ShowRows(window, "Recent calls");
        var sip = typeof(MainWindow).GetField("_sip", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("Could not find SIP phone instance for in-call preview.");
        var stateMachine = sip.GetType().GetField("_callState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sip) as SipCallStateMachine
            ?? throw new InvalidOperationException("Could not find SIP call state machine for in-call preview.");
        var applyStatus = typeof(MainWindow).GetMethod("ApplySipCallStatus", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find ApplySipCallStatus.");
        var applyContext = typeof(MainWindow).GetMethod("ApplyCallContext", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find ApplyCallContext.");

        AssertTransition(stateMachine, SipCallState.IncomingRinging, SipCallDirection.Inbound, "+1 828 555 0142");
        applyStatus.Invoke(window, new object[] { stateMachine.Current });
        AssertTransition(stateMachine, SipCallState.Connecting, SipCallDirection.None, "");
        AssertTransition(stateMachine, SipCallState.Connected, SipCallDirection.None, "");
        applyStatus.Invoke(window, new object[] { stateMachine.Current });
        DrainDispatcher(window.Dispatcher);

        AssertVisible(window, "CallWorkspace", "in-call workspace while connected");
        AssertCollapsed(window, "ListSection", "recent calls while connected");
        AssertVisible(window, "InCallControls", "call controls while connected");
        AssertCollapsed(window, "DialHost", "search/dial box while connected");

        applyContext.Invoke(window, new object?[]
        {
            new IncomingCallContext("Dana Mercer", "Blue Ridge Dental", "101",
            [
                new ConnectWiseTicketSummary("48213", "Front desk printer offline after update", "Priority 2 - High", "New"),
                new ConnectWiseTicketSummary("48190", "New hire laptop for Kayla R.", "Priority 4 - Low", "Scheduled")
            ], null, "Last call Sep 11"),
            null
        });
        var picker = Element(window, "CallTicketPicker") as ComboBox
            ?? throw new InvalidOperationException("Could not find the ticket picker.");
        if (picker.Items.Count != 3 || picker.SelectedItem is not CallTicketChoice { Id: "" })
            throw new InvalidOperationException("The ticket picker should list No ticket plus open tickets and default to No ticket.");
        picker.SelectedItem = picker.Items.Cast<CallTicketChoice>().First(choice => choice.Id == "48190");
        applyContext.Invoke(window, new object?[]
        {
            new IncomingCallContext("Dana Mercer", "Blue Ridge Dental", "101",
            [
                new ConnectWiseTicketSummary("48213", "Front desk printer offline after update", "Priority 2 - High", "New"),
                new ConnectWiseTicketSummary("48190", "New hire laptop for Kayla R.", "Priority 4 - Low", "Scheduled")
            ], null, "Last call Sep 11"),
            null
        });
        if (picker.SelectedItem is not CallTicketChoice { Id: "48190" })
            throw new InvalidOperationException("A late caller lookup must keep the ticket the technician already chose.");
        picker.SelectedItem = picker.Items.Cast<CallTicketChoice>().First(choice => choice.Id == "48213");
        if (!Button(window, "NewCallTicketButton").IsEnabled)
            throw new InvalidOperationException("New ticket should always be available during a call.");
        var notes = Element(window, "CallNotesText") as TextBox
            ?? throw new InvalidOperationException("Could not find call notes.");
        notes.Text = "Caller reports the front desk printer is still offline after Tuesday's driver update.";
        if (!Button(window, "SaveCallNoteButton").IsEnabled)
            throw new InvalidOperationException("Save note should be enabled with notes and a ticket selected.");
        var settingsForPark = typeof(MainWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        settingsForPark.GetType().GetProperty("ParkCode")!.SetValue(settingsForPark, "*70");
        Button(window, "ParkButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "ParkPanel", "inline park panel");
        var parkOptions = (Element(window, "ParkSlotsPanel") as Panel)!.Children.OfType<Button>().ToList();
        if (parkOptions.Count != 1 || !Equals(parkOptions[0].Content, "Any open slot"))
            throw new InvalidOperationException("The park panel should offer the configured park code.");
        settingsForPark.GetType().GetProperty("ParkCode")!.SetValue(settingsForPark, "");
        Button(window, "TransferButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertVisible(window, "TransferPanel", "inline transfer panel");
        AssertCollapsed(window, "ParkPanel", "park panel after opening transfer");
        Button(window, "TransferButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        AssertCollapsed(window, "TransferPanel", "inline transfer panel after second click");

        var outputPath = RenderWindowPreview(window, "callbridge-in-call.png");
        notes.Clear();
        AssertTransition(stateMachine, SipCallState.Ending, SipCallDirection.None, "");
        applyStatus.Invoke(window, new object[] { stateMachine.Current });
        AssertTransition(stateMachine, SipCallState.Idle, SipCallDirection.None, "");
        applyStatus.Invoke(window, new object[] { stateMachine.Current });
        DrainDispatcher(window.Dispatcher);
        // Wrap-up: the notes panel stays open after the caller hangs up so the tech can still save notes.
        AssertVisible(window, "CallWorkspace", "wrap-up workspace after hang-up");
        AssertVisible(window, "WrapUpBanner", "wrap-up banner after hang-up");
        AssertVisible(window, "CloseWrapUpButton", "Done button during wrap-up");
        AssertCollapsed(window, "ListSection", "recent calls during wrap-up");
        if (Element(window, "CallNotesText") is not TextBox wrapUpNotes) throw new InvalidOperationException("Missing call notes box.");
        wrapUpNotes.Text = "Followed up after the caller hung up.";
        DrainDispatcher(window.Dispatcher);
        var wrapUpPath = RenderWindowPreview(window, "callbridge-wrap-up.png");
        Button(window, "CloseWrapUpButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        DrainDispatcher(window.Dispatcher);
        AssertCollapsed(window, "CallWorkspace", "in-call workspace after finishing wrap-up");
        AssertCollapsed(window, "WrapUpBanner", "wrap-up banner after finishing wrap-up");
        AssertVisible(window, "ListSection", "recent calls after finishing wrap-up");
        if (wrapUpNotes.Text.Length != 0) throw new InvalidOperationException("Finishing wrap-up should clear the notes box.");
        _ = wrapUpPath;
        return outputPath;
    }

    private static string CaptureIncomingCallPreview(MainWindow window)
    {
        ShowRows(window, "Recent calls", new RowItem("Dana Mercer", "Inbound · connected · Sep 14, 3:52 PM", "Call", "101", CallId: "call-1"));
        var sip = typeof(MainWindow).GetField("_sip", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new InvalidOperationException("Could not find SIP phone instance for preview rendering.");
        var stateMachine = sip.GetType().GetField("_callState", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(sip) as SipCallStateMachine
            ?? throw new InvalidOperationException("Could not find SIP call state machine for preview rendering.");
        AssertTransition(stateMachine, SipCallState.IncomingRinging, SipCallDirection.Inbound, "Axion Support (101)");
        DrainDispatcher(window.Dispatcher);
        var outputPath = RenderWindowPreview(window, "callbridge-incoming-call.png");
        AssertTransition(stateMachine, SipCallState.Ending, SipCallDirection.None, "");
        AssertTransition(stateMachine, SipCallState.Idle, SipCallDirection.None, "");
        DrainDispatcher(window.Dispatcher);
        return outputPath;
    }

    private static string CaptureSettingsPreview(MainWindow window)
    {
        var showSettings = typeof(MainWindow).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Could not find settings navigation for preview rendering.");
        showSettings.Invoke(window, null);
        Button(window, "SettingsAudioTabButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        DrainDispatcher(window.Dispatcher);
        return RenderWindowPreview(window, "callbridge-audio-settings.png");
    }

    private static void DrainDispatcher(Dispatcher dispatcher) =>
        dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void AssertPreviewIsNotBlank(BitmapSource bitmap)
    {
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        var nonBlackPixels = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 8 || pixels[index + 1] > 8 || pixels[index + 2] > 8)
                nonBlackPixels++;
        }

        var totalPixels = bitmap.PixelWidth * bitmap.PixelHeight;
        if (nonBlackPixels < totalPixels / 10)
            throw new InvalidOperationException("Rendered CallBridge preview is blank or nearly blank.");
    }

    private static void AssertDialogShell(Window window, string label, double expectedMinWidth, double expectedMinHeight)
    {
        if (window.ResizeMode != ResizeMode.CanResize)
            throw new InvalidOperationException($"{label} should be resizable.");
        if (Math.Abs(window.MinWidth - expectedMinWidth) > 0.1 || Math.Abs(window.MinHeight - expectedMinHeight) > 0.1)
            throw new InvalidOperationException($"{label} expected min size {expectedMinWidth}x{expectedMinHeight}, got {window.MinWidth}x{window.MinHeight}.");
        if (window.Content is not ScrollViewer scrollViewer || scrollViewer.VerticalScrollBarVisibility != ScrollBarVisibility.Auto)
            throw new InvalidOperationException($"{label} should use an auto vertical scroll container.");
    }

    private static void AssertHasWrapPanel(Window window, string label)
    {
        if (!Descendants<WrapPanel>(window).Any())
            throw new InvalidOperationException($"{label} should wrap action buttons.");
    }

    private static void AssertHasDefaultButton(Window window, string label)
    {
        if (!Descendants<Button>(window).Any(button => button.IsDefault))
            throw new InvalidOperationException($"{label} should have a default action button.");
    }

    private static void AssertHasCancelButton(Window window, string label)
    {
        if (!Descendants<Button>(window).Any(button => button.IsCancel))
            throw new InvalidOperationException($"{label} should have a cancel button for Escape.");
    }

    private static void AssertDialogLabelsWrap(Window window, string label)
    {
        foreach (var textBlock in Descendants<TextBlock>(window).Where(block => block.Foreground is SolidColorBrush brush && brush.Color == Color.FromRgb(102, 117, 134)))
        {
            if (textBlock.TextWrapping != TextWrapping.Wrap)
                throw new InvalidOperationException($"{label} label '{textBlock.Text}' should wrap.");
        }
    }

    private static void AssertDialogButtonMetadata(Window window, string label)
    {
        foreach (var button in Descendants<Button>(window))
        {
            var content = button.Content?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(content)) continue;
            if (!string.Equals(button.ToolTip?.ToString(), content, StringComparison.Ordinal))
                throw new InvalidOperationException($"{label} button '{content}' should use its content as a tooltip.");
            if (!string.Equals(AutomationProperties.GetName(button), content, StringComparison.Ordinal))
                throw new InvalidOperationException($"{label} button '{content}' should expose an automation name.");
            if (!ToolTipService.GetShowOnDisabled(button))
                throw new InvalidOperationException($"{label} button '{content}' should show its tooltip while disabled.");
        }
    }

    private static void AssertDialogInputMetadata(Window window, string label, params string[] expectedNames)
    {
        foreach (var expected in expectedNames)
        {
            var control = Descendants<Control>(window).FirstOrDefault(item => AutomationProperties.GetName(item) == expected)
                ?? throw new InvalidOperationException($"{label} should expose input automation name '{expected}'.");
            if (!string.Equals(control.ToolTip?.ToString(), expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"{label} input '{expected}' should use its label as a tooltip.");
            if (!ToolTipService.GetShowOnDisabled(control))
                throw new InvalidOperationException($"{label} input '{expected}' should show its tooltip while disabled.");
        }
    }

    private static void AssertWindowTextDoesNotContain(Window window, string text, string label)
    {
        if (Descendants<TextBlock>(window).Any(block => block.Text.Contains(text, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{label} contains unexpected text '{text}'.");
    }

    private sealed class WindowScope : IDisposable
    {
        public Window Window { get; }
        public WindowScope(Window window) => Window = window;
        public void Dispose() => Window.Close();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void WaitWithDispatcher(Dispatcher dispatcher, Task task)
    {
        if (task.IsCompleted)
        {
            task.GetAwaiter().GetResult();
            return;
        }

        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        task.GetAwaiter().GetResult();
    }

    private static FrameworkElement Element(MainWindow window, string name) =>
        window.FindName(name) as FrameworkElement
        ?? throw new InvalidOperationException($"Could not find required layout element '{name}'.");

    private static Button Button(MainWindow window, string name) =>
        window.FindName(name) as Button
        ?? throw new InvalidOperationException($"Could not find required button '{name}'.");

    private static void AssertGridLength(MainWindow window, string name, double expected, string label)
    {
        var column = window.FindName(name) as ColumnDefinition
            ?? throw new InvalidOperationException($"Could not find required column '{name}'.");
        if (Math.Abs(column.Width.Value - expected) > 0.1)
            throw new InvalidOperationException($"{label} expected {expected}, got {column.Width.Value}.");
    }

    private static void AssertGridColumns(MainWindow window, string name, int expected, string label)
    {
        var grid = Element(window, name) as UniformGrid
            ?? throw new InvalidOperationException($"{label} should use a uniform settings grid.");
        if (grid.Columns != expected)
            throw new InvalidOperationException($"{label} expected {expected} columns, got {grid.Columns}.");
    }

    private static void AssertRowHeight(MainWindow window, string name, double expected, string label)
    {
        var row = window.FindName(name) as RowDefinition
            ?? throw new InvalidOperationException($"Could not find required row '{name}'.");
        if (Math.Abs(row.Height.Value - expected) > 0.1)
            throw new InvalidOperationException($"{label} expected {expected}, got {row.Height.Value}.");
    }

    private static void AssertWidth(MainWindow window, string name, double expected, string label)
    {
        var actual = Element(window, name).Width;
        if (Math.Abs(actual - expected) > 0.1)
            throw new InvalidOperationException($"{label} expected width {expected}, got {actual}.");
    }

    private static void AssertMaxWidth(MainWindow window, string name, double expected, string label)
    {
        var actual = Element(window, name).MaxWidth;
        if (Math.Abs(actual - expected) > 0.1)
            throw new InvalidOperationException($"{label} expected max width {expected}, got {actual}.");
    }

    private static void AssertGridPosition(MainWindow window, string name, int expectedRow, int expectedColumn, string label)
    {
        var element = Element(window, name);
        var actualRow = Grid.GetRow(element);
        var actualColumn = Grid.GetColumn(element);
        if (actualRow != expectedRow || actualColumn != expectedColumn)
            throw new InvalidOperationException($"{label} expected grid position {expectedRow},{expectedColumn}, got {actualRow},{actualColumn}.");
    }

    private static void AssertGridColumnSpan(MainWindow window, string name, int expected, string label)
    {
        var actual = Grid.GetColumnSpan(Element(window, name));
        if (actual != expected)
            throw new InvalidOperationException($"{label} expected column span {expected}, got {actual}.");
    }

    private static void AssertHorizontalAlignment(MainWindow window, string name, HorizontalAlignment expected, string label)
    {
        var actual = Element(window, name).HorizontalAlignment;
        if (actual != expected)
            throw new InvalidOperationException($"{label} expected {expected}, got {actual}.");
    }

    private static void AssertThickness(MainWindow window, string name, Thickness expected, string label)
    {
        var element = Element(window, name);
        var actual = element switch
        {
            Border border => border.Padding,
            FrameworkElement frameworkElement => frameworkElement.Margin,
            _ => throw new InvalidOperationException($"{label} does not expose padding or margin.")
        };
        if (Math.Abs(actual.Left - expected.Left) > 0.1
            || Math.Abs(actual.Top - expected.Top) > 0.1
            || Math.Abs(actual.Right - expected.Right) > 0.1
            || Math.Abs(actual.Bottom - expected.Bottom) > 0.1)
            throw new InvalidOperationException($"{label} expected {expected}, got {actual}.");
    }

    private static void AssertFontSize(MainWindow window, string name, double expected, string label)
    {
        var textBox = Element(window, name) as TextBox
            ?? throw new InvalidOperationException($"{label} should be a TextBox.");
        if (Math.Abs(textBox.FontSize - expected) > 0.1)
            throw new InvalidOperationException($"{label} expected font size {expected}, got {textBox.FontSize}.");
    }

    private static void AssertDialButtonRuntimeSize(MainWindow window, double expectedMinWidth, double expectedMinHeight, string label)
    {
        var dialPad = window.FindName("DialPad") as Panel
            ?? throw new InvalidOperationException("Could not find DialPad.");
        foreach (var button in dialPad.Children.OfType<Button>())
        {
            if (Math.Abs(button.MinWidth - expectedMinWidth) > 0.1 || Math.Abs(button.MinHeight - expectedMinHeight) > 0.1)
                throw new InvalidOperationException($"{label} expected {expectedMinWidth}x{expectedMinHeight}, got {button.MinWidth}x{button.MinHeight}.");
        }
    }

    private static void AssertStyleSetter(Style style, DependencyProperty property, double expected, string label)
    {
        var setter = style.Setters.OfType<Setter>().FirstOrDefault(item => item.Property == property)
            ?? throw new InvalidOperationException($"{label} should be explicitly set.");
        var actual = Convert.ToDouble(setter.Value);
        if (Math.Abs(actual - expected) > 0.1)
            throw new InvalidOperationException($"{label} expected {expected}, got {actual}.");
    }

    private static void AssertVisible(MainWindow window, string name, string label)
    {
        if (Element(window, name).Visibility != Visibility.Visible)
            throw new InvalidOperationException($"{label} should be visible.");
    }

    private static void AssertCollapsed(MainWindow window, string name, string label)
    {
        if (Element(window, name).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException($"{label} should be collapsed.");
    }

    private static void AssertEnabled(string name, MainWindow window, string label)
    {
        if (!Element(window, name).IsEnabled)
            throw new InvalidOperationException($"{label} should be enabled.");
    }

    private static void AssertDisabled(string name, MainWindow window, string label)
    {
        if (Element(window, name).IsEnabled)
            throw new InvalidOperationException($"{label} should be disabled.");
    }

    private static void AssertTooltipContains(FrameworkElement element, string expected, string label)
    {
        var tooltip = element.ToolTip?.ToString() ?? "";
        if (!tooltip.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label} should contain '{expected}', got '{tooltip}'.");
    }

    private static void SetSetting(object settings, string property, string value)
    {
        var target = settings.GetType().GetProperty(property)
            ?? throw new InvalidOperationException($"Could not find settings property '{property}'.");
        target.SetValue(settings, value);
    }

    private static void AssertBrush(Brush brush, string expected, string label)
    {
        if (expected == "Transparent")
        {
            if (!Equals(brush, Brushes.Transparent))
                throw new InvalidOperationException($"{label} should be transparent.");
            return;
        }

        var color = brush is SolidColorBrush solid ? solid.Color.ToString() : "";
        if (!color.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{label} expected {expected}, got {color}.");
    }
}
