using System.Windows;
using System.Windows.Controls;

namespace Lumio.App.Views;

public partial class AdminPinDialog : Window
{
    private readonly string _expectedPin;
    private string _enteredPin = string.Empty;

    public bool IsAuthenticated { get; private set; }

    public AdminPinDialog(string expectedPin, string? title = null, string? subtitle = null, string? hint = null)
    {
        InitializeComponent();
        _expectedPin = expectedPin;
        if (title is not null) TitleText.Text = title;
        if (subtitle is not null) SubtitleText.Text = subtitle;
        if (hint is not null) HintText.Text = hint;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (string.IsNullOrEmpty(_enteredPin))
        {
            PinDisplayText.Text = "____";
        }
        else
        {
            PinDisplayText.Text = new string('•', _enteredPin.Length) + new string('_', Math.Max(0, 4 - _enteredPin.Length));
        }
    }

    private void OnDigitClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Content is string digit)
        {
            if (_enteredPin.Length < 8)
            {
                _enteredPin += digit;
                UpdateDisplay();
                ErrorMessageText.Visibility = Visibility.Collapsed;

                if (_enteredPin.Length >= _expectedPin.Length)
                {
                    CheckPin();
                }
            }
        }
    }

    private void CheckPin()
    {
        if (_enteredPin == _expectedPin)
        {
            IsAuthenticated = true;
            DialogResult = true;
            Close();
        }
        else
        {
            ErrorMessageText.Text = "Falscher Code! Bitte erneut versuchen.";
            ErrorMessageText.Visibility = Visibility.Visible;
            _enteredPin = string.Empty;
            UpdateDisplay();
        }
    }

    private void OnBackspaceClick(object sender, RoutedEventArgs e)
    {
        if (_enteredPin.Length > 0)
        {
            _enteredPin = _enteredPin[..^1];
            UpdateDisplay();
            ErrorMessageText.Visibility = Visibility.Collapsed;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _enteredPin = string.Empty;
        UpdateDisplay();
        ErrorMessageText.Visibility = Visibility.Collapsed;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
