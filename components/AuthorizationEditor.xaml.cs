using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace northApi.components;

public partial class AuthorizationEditor : UserControl
{
    public AuthorizationEditor()
    {
        InitializeComponent();
        UpdateFields();
    }

    private string AuthType => (AuthTypeSelector.SelectedItem as ComboBoxItem)?.Tag as string ?? "inherit";
    private void AuthType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AuthMessage is not null) UpdateFields();
    }

    private void UpdateFields()
    {
        ApiKeyFields.Visibility = AuthType == "apiKey" ? Visibility.Visible : Visibility.Collapsed;
        BasicFields.Visibility = AuthType == "basic" ? Visibility.Visible : Visibility.Collapsed;
        TokenFields.Visibility = AuthType is "bearer" or "oauth2" ? Visibility.Visible : Visibility.Collapsed;
        AuthMessage.Visibility = AuthType is "inherit" or "none" ? Visibility.Visible : Visibility.Collapsed;
        AuthMessage.Text = AuthType == "none"
            ? "This request does not use an authorization helper."
            : "This request is not inheriting any authorization helper at the moment. Collection authorization has not been configured.";
        AuthDescription.Text = AuthType == "apiKey"
            ? "The API key will be added to the request header or query parameters when you send the request."
            : AuthType == "none"
                ? "No authorization will be generated. Headers you enter manually are still sent."
                : "The authorization header will be automatically generated when you send the request.";
        TokenLabel.Text = AuthType == "oauth2" ? "Access Token" : "Token";
        TokenHelp.Text = AuthType == "oauth2"
            ? "Paste an existing OAuth 2.0 access token. Token acquisition and refresh are not available yet."
            : "The token is sent in the Authorization header using the Bearer scheme.";
    }

    public northApi.models.AuthDefinition Snapshot() => new()
    {
        Type = AuthType, Username = UsernameBox.Text, Password = PasswordInput.Password,
        Token = TokenInput.Text, ApiKeyName = ApiKeyName.Text, ApiKeyValue = ApiKeyValue.Text,
        ApiKeyInQuery = ApiKeyLocation.SelectedIndex == 1
    };

    public void Load(northApi.models.AuthDefinition auth)
    {
        AuthTypeSelector.SelectedItem = AuthTypeSelector.Items.Cast<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, auth.Type)) ?? AuthTypeSelector.Items[0];
        UsernameBox.Text = auth.Username; PasswordInput.Password = auth.Password; TokenInput.Text = auth.Token;
        ApiKeyName.Text = auth.ApiKeyName; ApiKeyValue.Text = auth.ApiKeyValue;
        ApiKeyLocation.SelectedIndex = auth.ApiKeyInQuery ? 1 : 0;
        UpdateFields();
    }
}
