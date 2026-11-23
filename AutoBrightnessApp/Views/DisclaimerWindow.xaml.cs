using System.Windows;
using System.Windows.Controls;
using AutoBrightnessApp.Services;

namespace AutoBrightnessApp.Views;

public partial class DisclaimerWindow : Window
{
    private readonly bool _isInitializing;
    private readonly bool _isAboutMode;

    public DisclaimerWindow(bool isAboutMode = false)
    {
        _isInitializing = true;
        _isAboutMode = isAboutMode;
        InitializeComponent();

        var appIcon = AppIconGenerator.GetAppIcon();
        Icon = appIcon;
        HeaderIcon.Source = appIcon;

        LanguageCombo.ItemsSource = LocalizationService.SupportedLanguages;
        LanguageCombo.SelectedValue = LocalizationService.CurrentLanguage;

        if (_isAboutMode)
        {
            ExitBtn.Visibility = Visibility.Collapsed;
            AgreeBtn.Visibility = Visibility.Collapsed;
            CloseBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ExitBtn.Visibility = Visibility.Visible;
            AgreeBtn.Visibility = Visibility.Visible;
            CloseBtn.Visibility = Visibility.Collapsed;
        }

        ApplyTexts();
        _isInitializing = false;
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || LanguageCombo.SelectedValue is not string code) return;
        LocalizationService.SetLanguage(code);
        ApplyTexts();
    }

    private void ApplyTexts()
    {
        FlowDirection = LocalizationService.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        AuthorText.Text = LocalizationService.Get("AuthorText");
        LanguageLabel.Text = LocalizationService.Get("LanguageLabel");
        OverviewTitleText.Text = LocalizationService.Get("DisclaimerOverviewTitle");
        OverviewBodyText.Text = LocalizationService.Get("DisclaimerOverviewBody");
        CameraWarningTitleText.Text = LocalizationService.Get("DisclaimerCameraTitle");
        CameraWarningBodyText.Text = LocalizationService.Get("DisclaimerCameraBody");
        DisclaimerTitleText.Text = LocalizationService.Get("DisclaimerLiabilityTitle");
        DisclaimerBodyText.Text = LocalizationService.Get("DisclaimerLiabilityBody");
        AgreeBtn.Content = LocalizationService.Get("DisclaimerAgreeBtn");
        ExitBtn.Content = LocalizationService.Get("DisclaimerExitBtn");
        CloseBtn.Content = LocalizationService.Get("DisclaimerCloseBtn");
    }

    private void Agree_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}