using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Lumio.App.Avalonia.Views;

public partial class AdminPinDialog : Window
{
    private readonly string _expectedPin;
    private readonly StringBuilder _enteredPin = new();

    public bool IsAuthenticated { get; private set; }

    public AdminPinDialog() : this("1234")
    {
    }

    public AdminPinDialog(string expectedPin)
    {
        InitializeComponent();
        _expectedPin = string.IsNullOrWhiteSpace(expectedPin) ? "1234" : expectedPin;
        UpdateDisplay();
    }

    private void OnDigitClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string digit } && _enteredPin.Length < 8)
        {
            _enteredPin.Append(digit);
            UpdateDisplay();

            if (_enteredPin.Length == _expectedPin.Length)
            {
                ValidatePin();
            }
        }
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (_enteredPin.Length > 0)
        {
            _enteredPin.Remove(_enteredPin.Length - 1, 1);
            UpdateDisplay();
        }
        ErrorMessageText.Text = "";
    }

    private void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        ValidatePin();
    }

    private void ValidatePin()
    {
        if (_enteredPin.ToString() == _expectedPin)
        {
            IsAuthenticated = true;
            Close();
        }
        else
        {
            ErrorMessageText.Text = "Falsche PIN. Bitte erneut versuchen.";
            _enteredPin.Clear();
            UpdateDisplay();
        }
    }

    private void UpdateDisplay()
    {
        PinDisplayText.Text = _enteredPin.Length == 0 ? "••••" : new string('•', _enteredPin.Length);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
