namespace EVE.Components.Server
{
    /// <summary>A memory component that the in-game editor can load, save and restore.</summary>
    public interface IRomHost
    {
        void SetRomData(byte[] data, string sourceText);
        void Save();
        void Restore();
    }
}
