using AutoMarkerReID.Application;
using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace AutoMarkerReID.Windows;

public sealed class WindowsFileTrashService : IFileTrashService
{
    public Task MoveToRecycleBinAsync(IReadOnlyCollection<string> paths, CancellationToken cancellationToken)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }

        return Task.CompletedTask;
    }
}
