// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;

namespace Twinleaf.Core;

/// <summary>
/// The <c>.tio</c> log backing one window, mirroring the macOS app's
/// <c>TioLogDocument</c>.
///
/// Recording always writes to a temporary file, and saving snapshots that file
/// to the chosen path. That ordering is what lets an untitled window start
/// recording immediately and keep recording across the save — the core is
/// appending to a path that never moves.
/// </summary>
public sealed class TioDocument : IDisposable
{
    private bool _disposed;

    public TioDocument()
    {
        TemporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"Twinleaf-{Guid.NewGuid():N}.tio");

        EnsureTemporaryFile();
        AppLog.Debug($"document temporary log: {TemporaryPath}");
    }

    /// <summary>Where the core appends packets. Stable for the document's life.</summary>
    public string TemporaryPath { get; }

    /// <summary>Where the user saved it, or null while untitled.</summary>
    public string? SavedPath { get; private set; }

    /// <summary>True when this document was opened from an existing file.</summary>
    public bool LoadedFromFile { get; private init; }

    /// <summary>Bytes the log occupied when opened; drives inspection mode.</summary>
    public long InitialByteCount { get; private init; }

    /// <summary>An opened, non-empty file starts in inspection rather than live mode.</summary>
    public bool ShouldOpenForInspection => LoadedFromFile && InitialByteCount > 0;

    public string DisplayName =>
        SavedPath is { } path ? Path.GetFileName(path) : "Untitled.tio";

    public bool HasUnsavedRecording => SavedPath is null && ByteCount > 0;

    /// <summary>
    /// Size on disk. Reads the directory entry rather than the contents, so this
    /// stays cheap while a long recording is in progress.
    /// </summary>
    public long ByteCount
    {
        get
        {
            try
            {
                var info = new FileInfo(TemporaryPath);
                return info.Exists ? info.Length : 0;
            }
            catch (IOException)
            {
                return 0;
            }
        }
    }

    public bool IsEmpty => ByteCount == 0;

    /// <summary>
    /// Opens an existing log for inspection. The file is copied to the
    /// document's temporary path so the original is never written to — the core
    /// appends to the temporary file if recording resumes.
    /// </summary>
    public static TioDocument Open(string path)
    {
        var document = new TioDocument
        {
            SavedPath = path,
            LoadedFromFile = true,
            InitialByteCount = new FileInfo(path).Length,
        };

        File.Copy(path, document.TemporaryPath, overwrite: true);
        return document;
    }

    /// <summary>
    /// Snapshots the recording to <paramref name="path"/>. Copy, not move: the
    /// core may still hold the temporary file open and appending, and a move
    /// would pull the file out from under it mid-recording.
    /// </summary>
    public void SaveTo(string path)
    {
        EnsureTemporaryFile();
        File.Copy(TemporaryPath, path, overwrite: true);
        SavedPath = path;
        AppLog.Debug($"saved {ByteCount} byte(s) to {path}");
    }

    /// <summary>Re-saves to the path this document already has.</summary>
    public bool Save()
    {
        if (SavedPath is not { } path)
        {
            return false;
        }

        SaveTo(path);
        return true;
    }

    private void EnsureTemporaryFile()
    {
        if (!File.Exists(TemporaryPath))
        {
            using var _ = File.Create(TemporaryPath);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (File.Exists(TemporaryPath))
            {
                File.Delete(TemporaryPath);
            }
        }
        catch (IOException exception)
        {
            // The core may still hold it open during teardown; a stray file in
            // the temp directory is not worth failing a window close over.
            AppLog.Debug($"could not remove {TemporaryPath}: {exception.Message}");
        }
    }
}
