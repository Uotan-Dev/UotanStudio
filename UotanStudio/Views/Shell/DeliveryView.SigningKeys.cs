using UotanStudio.Services;
using UotanStudio.Services.Keys;
using UotanStudio.ViewModels.Shell;

namespace UotanStudio.Views.Shell;

public partial class DeliveryView
{
    private readonly KeyLibraryService _payloadKeyLibrary;
    private IReadOnlyList<ManagedKey> _payloadKeys = [];
    private bool _selectingPayloadKey;
    private string _payloadSigningFingerprint = "";
    private string _payloadSigningIdentityPath = "";
    private string _legacyPayloadSignatureFile = "";
    private IReadOnlyList<ManagedKey> _zipSigningKeys = [];
    private bool _selectingZipSigningKey;
    private string _zipSigningIdentityPath = "";
    private string _zipSigningCertificatePath = "";

    private void InitializePayloadSigningKeys()
    {
        PayloadSigningKeyBox.DropDownOpened += (_, _) => RefreshPayloadSigningKeys();
        PayloadSigningKeyBox.SelectionChanged += (_, _) =>
        {
            if (_selectingPayloadKey) return;
            var index = PayloadSigningKeyBox.SelectedIndex;
            if (index < 0 || index > _payloadKeys.Count) return;
            _selectingPayloadKey = true;
            try
            {
                var key = index == 0 ? null : _payloadKeys[index - 1];
                _payloadSigningIdentityPath = key is null ? "" : _payloadKeyLibrary.PrivatePath(key);
                _payloadSigningFingerprint = key?.Fingerprint ?? "";
                _legacyPayloadSignatureFile = "";
                QueueDeliveryChange();
            }
            finally { _selectingPayloadKey = false; }
        };
        ManagePayloadKeysButton.Click += (_, _) => MenuViewModel.ShowKeyManagerWindow();
        RefreshPayloadSigningKeys();
    }

    private void RefreshPayloadSigningKeys()
    {
        _selectingPayloadKey = true;
        try
        {
            _payloadKeys = _payloadKeyLibrary.List().Where(k => k.Kind == ManagedKeyKind.Payload).ToArray();
            PayloadSigningKeyBox.ItemsSource = new[] { LanguageService.Instance.GetString("Keys_Unsigned") }
                .Concat(_payloadKeys.Select(k => $"{k.Name} · RSA {k.Bits} · {k.Fingerprint[^12..]}")).ToArray();
            var path = _payloadSigningIdentityPath.Trim();
            PayloadSigningKeyBox.SelectedIndex = path.Length == 0 ? 0 :
                _payloadKeys.Select((key, index) => new { key, index })
                    .Where(p => string.Equals(_payloadKeyLibrary.PrivatePath(p.key), path,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    .Select(p => p.index + 1).DefaultIfEmpty(-1).First();
        }
        catch (Exception ex) { Composition.Model.Status = ex.Message; }
        finally { _selectingPayloadKey = false; }
    }

    private void InitializeZipSigningKeys()
    {
        ZipSigningKeyBox.DropDownOpened += (_, _) => RefreshZipSigningKeys();
        ZipSigningKeyBox.SelectionChanged += (_, _) =>
        {
            if (_selectingZipSigningKey) return;
            var index = ZipSigningKeyBox.SelectedIndex;
            if (index < 0 || index > _zipSigningKeys.Count) return;
            _selectingZipSigningKey = true;
            try
            {
                var key = index == 0 ? null : _zipSigningKeys[index - 1];
                _zipSigningIdentityPath = key is null ? "" : _payloadKeyLibrary.PrivatePath(key);
                _zipSigningCertificatePath = key is null ? "" : _payloadKeyLibrary.EnsureCertificate(key);
                QueueDeliveryChange();
            }
            catch (Exception ex) { Composition.Model.Status = ex.Message; }
            finally { _selectingZipSigningKey = false; }
        };
        RefreshZipSigningKeys();
    }

    private void RefreshZipSigningKeys()
    {
        _selectingZipSigningKey = true;
        try
        {
            _zipSigningKeys = _payloadKeyLibrary.List()
                .Where(k => k.Kind == ManagedKeyKind.Zip)
                .ToArray();
            ZipSigningKeyBox.ItemsSource = new[] { LanguageService.Instance.GetString("Keys_Unsigned") }
                .Concat(_zipSigningKeys.Select(k => $"{k.Name} · RSA {k.Bits} · {k.Fingerprint[^12..]}"))
                .ToArray();
            var path = _zipSigningIdentityPath.Trim();
            ZipSigningKeyBox.SelectedIndex = path.Length == 0 ? 0 :
                _zipSigningKeys.Select((key, index) => new { key, index })
                    .Where(p => string.Equals(_payloadKeyLibrary.PrivatePath(p.key), path,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    .Select(p => p.index + 1).DefaultIfEmpty(-1).First();
        }
        catch (Exception ex) { Composition.Model.Status = ex.Message; }
        finally { _selectingZipSigningKey = false; }
    }
}