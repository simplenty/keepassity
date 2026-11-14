// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Runtime.CompilerServices;

namespace Keepassity.App;

/// <summary>
/// Attached properties that localize static XAML text through <see cref="Loc"/>.
///
/// <code>
/// &lt;TextBlock loc:Loc.Text="Recent databases" /&gt;
/// &lt;Button loc:Loc.ToolTip="Copy" loc:Loc.Name="Copy username" /&gt;
/// </code>
///
/// The English source string is the catalog key. Each registration is remembered
/// per element and re-applied when <see cref="Loc.LanguageChanged"/> fires, which
/// is what makes the language switch take effect without reloading the XAML.
/// Use these only for strings that code-behind never assigns; text that the code
/// sets (entry data, status lines, dialog content) goes through <c>Loc.T</c> at the
/// assignment site.
/// </summary>
public static class LocMarkup
{
    private sealed class Registration
    {
        public string? Text;
        public string? ToolTip;
        public string? Name;
        public string? HelpText;
    }

    private static readonly ConditionalWeakTable<DependencyObject, Registration> Registrations =
        new();

    // ------------------------------------------------------------ primary text

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(LocMarkup),
        new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value)
        => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement frameworkElement)
        {
            return;
        }
        Registration registration = GetRegistration(frameworkElement);
        registration.Text = args.NewValue as string;
        ApplyText(frameworkElement, registration.Text);
    }

    // ------------------------------------------------------------ tool tip

    public static readonly DependencyProperty ToolTipProperty = DependencyProperty.RegisterAttached(
        "ToolTip", typeof(string), typeof(LocMarkup),
        new PropertyMetadata(null, OnToolTipChanged));

    public static string? GetToolTip(DependencyObject element)
        => (string?)element.GetValue(ToolTipProperty);

    public static void SetToolTip(DependencyObject element, string? value)
        => element.SetValue(ToolTipProperty, value);

    private static void OnToolTipChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement frameworkElement)
        {
            return;
        }
        Registration registration = GetRegistration(frameworkElement);
        registration.ToolTip = args.NewValue as string;
        ApplyToolTip(frameworkElement, registration.ToolTip);
    }

    // ------------------------------------------------------------ accessibility

    public static readonly DependencyProperty NameProperty = DependencyProperty.RegisterAttached(
        "Name", typeof(string), typeof(LocMarkup),
        new PropertyMetadata(null, OnNameChanged));

    public static string? GetName(DependencyObject element) => (string?)element.GetValue(NameProperty);

    public static void SetName(DependencyObject element, string? value)
        => element.SetValue(NameProperty, value);

    private static void OnNameChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement frameworkElement)
        {
            return;
        }
        Registration registration = GetRegistration(frameworkElement);
        registration.Name = args.NewValue as string;
        ApplyName(frameworkElement, registration.Name);
    }

    public static readonly DependencyProperty HelpTextProperty = DependencyProperty.RegisterAttached(
        "HelpText", typeof(string), typeof(LocMarkup),
        new PropertyMetadata(null, OnHelpTextChanged));

    public static string? GetHelpText(DependencyObject element)
        => (string?)element.GetValue(HelpTextProperty);

    public static void SetHelpText(DependencyObject element, string? value)
        => element.SetValue(HelpTextProperty, value);

    private static void OnHelpTextChanged(DependencyObject element,
        DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement frameworkElement)
        {
            return;
        }
        Registration registration = GetRegistration(frameworkElement);
        registration.HelpText = args.NewValue as string;
        ApplyHelpText(frameworkElement, registration.HelpText);
    }

    // ------------------------------------------------------------ switch handler

    static LocMarkup()
    {
        Loc.LanguageChanged += (_, _) => ReapplyAll();
    }

    private static Registration GetRegistration(DependencyObject element)
        => Registrations.GetValue(element, _ => new Registration());

    /// <summary>Re-applies every registered string; also used by the shell's smoke tests.</summary>
    public static void ReapplyAll()
    {
        foreach (KeyValuePair<DependencyObject, Registration> pair in Registrations)
        {
            if (pair.Key is not FrameworkElement element)
            {
                continue;
            }
            if (pair.Value.Text is not null)
            {
                ApplyText(element, pair.Value.Text);
            }
            if (pair.Value.ToolTip is not null)
            {
                ApplyToolTip(element, pair.Value.ToolTip);
            }
            if (pair.Value.Name is not null)
            {
                ApplyName(element, pair.Value.Name);
            }
            if (pair.Value.HelpText is not null)
            {
                ApplyHelpText(element, pair.Value.HelpText);
            }
        }
    }

    // ------------------------------------------------------------ element mapping

    private static void ApplyText(FrameworkElement element, string? key)
    {
        if (key is null)
        {
            return;
        }
        string text = Loc.T(key);
        switch (element)
        {
            case TextBlock textBlock:
                textBlock.Text = text;
                break;
            case InfoBar infoBar:
                infoBar.Title = text;
                break;
            case NavigationViewItem navigationItem:
                navigationItem.Content = text;
                break;
            case MenuFlyoutItem menuItem:
                menuItem.Text = text;
                break;
            case TextBox textBox:
                textBox.PlaceholderText = text;
                break;
            case PasswordBox passwordBox:
                passwordBox.PlaceholderText = text;
                break;
            case AutoSuggestBox suggestBox:
                suggestBox.PlaceholderText = text;
                break;
            case ComboBoxItem comboItem:
                comboItem.Content = text;
                break;
            case HyperlinkButton link:
                link.Content = text;
                break;
            case Expander expander:
                expander.Header = text;
                break;
            case ToggleSwitch toggle:
                toggle.Header = text;
                break;
            case ButtonBase button:
                button.Content = text;
                break;
            case ContentControl contentControl:
                contentControl.Content = text;
                break;
            default:
                AppLog.Warn($"loc:Loc.Text is not supported on {element.GetType().Name}");
                break;
        }
    }

    private static void ApplyToolTip(FrameworkElement element, string? key)
    {
        if (key is null)
        {
            return;
        }
        ToolTipService.SetToolTip(element, Loc.T(key));
    }

    private static void ApplyName(FrameworkElement element, string? key)
    {
        if (key is null)
        {
            return;
        }
        AutomationProperties.SetName(element, Loc.T(key));
    }

    private static void ApplyHelpText(FrameworkElement element, string? key)
    {
        if (key is null)
        {
            return;
        }
        AutomationProperties.SetHelpText(element, Loc.T(key));
    }
}
