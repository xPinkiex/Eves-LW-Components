namespace EVE.Components.Server
{
    /// <summary>A memory component that the in-game editor can load, save and restore.</summary>
    public interface IRomHost
    {
        /// <summary>The most bytes the editor or an @file may load into it, from address 0.</summary>
        int MaxImageBytes { get; }
        void SetRomData(byte[] data, string sourceText);
        void Save();
        void Restore();
    }
}
