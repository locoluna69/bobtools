using System.Windows;

namespace BobTools.Views;

public partial class ReviewWindow : Window
{
    public string EditedText => EditorTextBox.Text;

    public ReviewWindow(string initialText, string? captureLabel = null, string? resourceSummary = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(captureLabel))
        {
            Title = $"Review {captureLabel} OCR Result";
        }

        if (!string.IsNullOrWhiteSpace(resourceSummary))
        {
            ResourceSummaryTextBlock.Text = resourceSummary;
            ResourceSummaryTextBlock.Visibility = Visibility.Visible;
        }

        EditorTextBox.Text = initialText;
        EditorTextBox.SelectAll();
        EditorTextBox.Focus();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
