namespace UotanStudio.Assets;

internal interface IAssetPayloadSource
{
    byte[] Read(string resourceName);
}