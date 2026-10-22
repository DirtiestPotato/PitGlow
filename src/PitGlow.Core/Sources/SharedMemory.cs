using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace PitGlow.Sources;

/// <summary>Read-only access to a block of game memory.</summary>
public interface IMemoryBlock
{
    bool Open();
    T Read<T>(int offset) where T : unmanaged;
    void Close();
}

/// <summary>
/// A named Windows shared-memory block, only ever opened, never created (creating it
/// would hand the game a block of the wrong size).
/// </summary>
public sealed class SharedBlock(string name) : IMemoryBlock
{
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;

    public string Name => name;

    public bool Open()
    {
        if (_view is not null) return true;
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            _map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
            _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (Exception)
        {
            Close();
            return false;
        }
    }

    public T Read<T>(int offset) where T : unmanaged
    {
        _view!.Read(offset, out T value);
        return value;
    }

    public void ReadBytes(int offset, byte[] buffer, int count) => _view!.ReadArray(offset, buffer, 0, count);

    public long Capacity => _view?.Capacity ?? 0;

    public void Close()
    {
        _view?.Dispose();
        _map?.Dispose();
        _view = null;
        _map = null;
    }
}

/// <summary>Test stand-in for SharedBlock backed by a byte array.</summary>
public sealed class BytesBlock(byte[]? data) : IMemoryBlock
{
    public bool Open() => data is not null;
    public T Read<T>(int offset) where T : unmanaged => MemoryMarshal.Read<T>(data.AsSpan(offset));
    public void Close() { }
}
