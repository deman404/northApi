using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using northApi.models;
using northApi.services;

namespace northApi.components;

public partial class BodyEditor : UserControl
{
    private readonly ObservableCollection<RequestField> multipartFields = [new()];
    private readonly ObservableCollection<RequestField> encodedFields = [new()];
    private string mode = "raw";
    private string? binaryPath;
    public BodyEditor() { InitializeComponent(); UpdateMode(); }
    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        mode = (sender as RadioButton)?.Tag as string ?? "raw";
        if (RawPanel is not null) UpdateMode();
    }
    private void CommitFields() { FormGrid.CommitEdit(DataGridEditingUnit.Cell, true); FormGrid.CommitEdit(DataGridEditingUnit.Row, true); }
    private void UpdateMode()
    {
        CommitFields();
        RawPanel.Visibility = mode == "raw" ? Visibility.Visible : Visibility.Collapsed;
        RawFormat.Visibility = RawPanel.Visibility;
        FormGrid.Visibility = mode is "form" or "urlencoded" ? Visibility.Visible : Visibility.Collapsed;
        BinaryPanel.Visibility = mode == "binary" ? Visibility.Visible : Visibility.Collapsed;
        GraphPanel.Visibility = mode == "graphql" ? Visibility.Visible : Visibility.Collapsed;
        NoneHint.Visibility = mode == "none" ? Visibility.Visible : Visibility.Collapsed;
        FormGrid.ItemsSource = mode == "form" ? multipartFields : encodedFields;
    }
    private void Raw_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LineNumbers is not null) LineNumbers.Text = string.Join(Environment.NewLine, Enumerable.Range(1, RawEditor.Text.Count(character => character == '\n') + 1));
    }
    private void Raw_ScrollChanged(object sender, ScrollChangedEventArgs e) => GutterScroll?.ScrollToVerticalOffset(RawEditor.VerticalOffset);
    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select binary request body", Filter = "All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        binaryPath = dialog.FileName; BinaryFileLabel.Text = binaryPath;
    }
    public BodyDefinition Snapshot()
    {
        CommitFields();
        return new() { Mode = mode, Raw = RawEditor.Text, ContentType = (RawFormat.SelectedItem as ComboBoxItem)?.Tag as string ?? "application/json", FormData = multipartFields.Select(field => field.Copy()).ToList(), Urlencoded = encodedFields.Select(field => field.Copy()).ToList(), BinaryPath = binaryPath, Query = GraphQuery.Text, Variables = GraphVariables.Text };
    }
    public void Load(BodyDefinition body)
    {
        RawEditor.Text = body.Raw;
        RawFormat.SelectedItem = RawFormat.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, body.ContentType)) ?? RawFormat.Items[0];
        multipartFields.Clear(); foreach (var field in body.FormData) multipartFields.Add(RequestField.From(field));
        encodedFields.Clear(); foreach (var field in body.Urlencoded) encodedFields.Add(RequestField.From(field));
        binaryPath = body.BinaryPath; BinaryFileLabel.Text = binaryPath ?? "No file selected";
        GraphQuery.Text = body.Query; GraphVariables.Text = body.Variables;
        var radio = WorkspaceDialogs.Descendants(this).OfType<RadioButton>().FirstOrDefault(item => Equals(item.Tag, body.Mode));
        if (radio is not null) radio.IsChecked = true;
        mode = body.Mode; UpdateMode();
    }
}
