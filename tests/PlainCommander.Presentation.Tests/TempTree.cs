namespace PlainCommander.Presentation.Tests;

/// <summary>A throwaway directory tree for one test.</summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = Directory.CreateTempSubdirectory("pc-vm-").FullName;
    }

    public string Root { get; }

    public string Dir(string relative) => Directory.CreateDirectory(Path.Combine(Root, relative)).FullName;

    public string File(string relative, int bytes = 0)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

internal sealed class RecordingLauncher : ILauncher
{
    public List<string> Opened { get; } = [];

    public List<(string Command, string Directory)> Commands { get; } = [];

    public void Open(string path, string workingDirectory) => Opened.Add(path);

    public List<(string Editor, string Path)> Edited { get; } = [];

    public void Edit(string editor, string path, string workingDirectory) => Edited.Add((editor, path));

    public void RunCommand(string command, string workingDirectory) => Commands.Add((command, workingDirectory));
}

internal sealed class FakeDialogs : IDialogService
{
    public string? TransferAnswer { get; set; }

    public string? NameAnswer { get; set; }

    public bool DeleteAnswer { get; set; } = true;

    public string? MaskAnswer { get; set; }

    public string? LastDefaultTarget { get; private set; }

    public string? LastNameInitial { get; private set; }

    public bool? LastDeletePermanent { get; private set; }

    public List<string> Errors { get; } = [];

    public Task<string?> AskTransferTargetAsync(Core.Operations.TransferKind kind, int count, string firstName, string defaultTarget)
    {
        LastDefaultTarget = defaultTarget;
        return Task.FromResult(TransferAnswer);
    }

    public string? PackAnswer { get; set; }

    public string? PackPassword { get; set; }

    public Task<PackAnswer?> AskPackTargetAsync(int count, string firstName, string defaultTarget, bool sevenZip)
    {
        LastDefaultTarget = defaultTarget;
        return Task.FromResult(PackAnswer is null ? null : new PackAnswer(PackAnswer, PackPassword));
    }

    public bool UndoAnswer { get; set; } = true;

    public List<string> UndoDetails { get; } = [];

    public Task<bool> ConfirmUndoAsync(string operationTitle, string details)
    {
        UndoDetails.Add(details);
        return Task.FromResult(UndoAnswer);
    }

    public EncryptAnswer? EncryptAnswer { get; set; }

    public bool DeleteOriginalsAnswer { get; set; }

    public int DeleteOriginalsQuestions { get; private set; }

    public string? LastEncryptFolder { get; private set; }

    public Task<EncryptAnswer?> AskEncryptAsync(int count, string firstName, bool sevenZip, string defaultFolder)
    {
        LastEncryptFolder = defaultFolder;
        return Task.FromResult(EncryptAnswer);
    }

    public Queue<string?> DecryptPasswords { get; } = new();

    public List<bool> DecryptWrongFlags { get; } = [];

    public Task<string?> AskDecryptPasswordAsync(string file, bool wrong)
    {
        DecryptWrongFlags.Add(wrong);
        return Task.FromResult(DecryptPasswords.Count > 0 ? DecryptPasswords.Dequeue() : null);
    }

    public Task<bool> ConfirmDeleteOriginalsAsync(int count)
    {
        DeleteOriginalsQuestions++;
        return Task.FromResult(DeleteOriginalsAnswer);
    }

    public UnpackAnswer? UnpackAnswer { get; set; }

    public Task<UnpackAnswer?> AskUnpackTargetAsync(int count, string firstName, string defaultTarget)
    {
        LastDefaultTarget = defaultTarget;
        return Task.FromResult(UnpackAnswer);
    }

    public Task<string?> AskNameAsync(NamePrompt prompt, string initial)
    {
        LastNameInitial = initial;
        return Task.FromResult(NameAnswer);
    }

    public Task<bool> ConfirmDeleteAsync(int files, int directories, string firstName, bool permanent)
    {
        LastDeletePermanent = permanent;
        return Task.FromResult(DeleteAnswer);
    }

    public Task<string?> AskMaskAsync(bool mark, string initial) => Task.FromResult(MaskAnswer);

    public string? FilterAnswer { get; set; }

    public Core.Navigation.PanelFilter? FilterInitial { get; private set; }

    /// <summary>Wins over <see cref="FilterAnswer"/> when set.</summary>
    public Core.Navigation.PanelFilter? FilterAnswerFull { get; set; }

    public Task<Core.Navigation.PanelFilter?> AskFilterAsync(Core.Navigation.PanelFilter? initial, bool createdKnown)
    {
        FilterInitial = initial;
        return Task.FromResult(FilterAnswerFull ?? (FilterAnswer is null ? null : Core.Navigation.PanelFilter.FromText(FilterAnswer)));
    }

    public List<string> Infos { get; } = [];

    public PlainCommander.Core.Compare.CompareOptions? CompareAnswer { get; set; } = new();

    public Task ShowInfoAsync(string title, string message)
    {
        Infos.Add(message);
        return Task.CompletedTask;
    }

    public Task<PlainCommander.Core.Compare.CompareOptions?> AskCompareOptionsAsync(PlainCommander.Core.Compare.CompareOptions initial) => Task.FromResult(CompareAnswer);

    public bool UploadBackAnswer { get; set; }

    public List<string> UploadBackQuestions { get; } = [];

    public Task<bool> ConfirmUploadBackAsync(string name, string server)
    {
        UploadBackQuestions.Add(name);
        return Task.FromResult(UploadBackAnswer);
    }

    public bool SyncAnswer { get; set; }

    public int SyncOffers { get; private set; }

    public Task<bool> OfferSyncAsync(int differences)
    {
        SyncOffers++;
        return Task.FromResult(SyncAnswer);
    }

    public Core.Operations.AttributeChange? AttributesAnswer { get; set; }

    public (FileAttributes Attributes, DateTimeOffset Modified)? LastAttributesInitial { get; private set; }

    public Task<Core.Operations.AttributeChange?> AskAttributesAsync(int count, string firstName, FileAttributes attributes, DateTimeOffset modified, bool anyFolder)
    {
        LastAttributesInitial = (attributes, modified);
        return Task.FromResult(AttributesAnswer);
    }

    public ChecksumAnswer? ChecksumAnswer { get; set; }

    public string? LastChecksumDefault { get; private set; }

    public Task<ChecksumAnswer?> AskChecksumAsync(int count, string defaultName)
    {
        LastChecksumDefault = defaultName;
        return Task.FromResult(ChecksumAnswer);
    }

    public PermissionsAnswer? PermissionsAnswer { get; set; }

    public int? LastPermissionsInitial { get; private set; }

    public Task<PermissionsAnswer?> AskPermissionsAsync(int count, string firstName, int mode, bool anyFolder)
    {
        LastPermissionsInitial = mode;
        return Task.FromResult(PermissionsAnswer);
    }

    public bool ElevateAnswer { get; set; }

    public List<string> ElevateQuestions { get; } = [];

    public Task<bool> ConfirmElevateAsync(string problem)
    {
        ElevateQuestions.Add(problem);
        return Task.FromResult(ElevateAnswer);
    }

    public Task ShowErrorAsync(string message)
    {
        Errors.Add(message);
        return Task.CompletedTask;
    }
}

internal sealed class NoPrompts : Core.Operations.IOperationPrompts
{
    public Task<Core.Operations.ConflictChoice> ResolveConflictAsync(Core.Operations.ConflictInfo conflict, CancellationToken cancellationToken) =>
        Task.FromResult(Core.Operations.ConflictChoice.Skip);

    public Task<Core.Operations.ErrorChoice> ResolveErrorAsync(string path, Exception exception, bool canElevate, CancellationToken cancellationToken) =>
        Task.FromResult(Core.Operations.ErrorChoice.Skip);
}

internal sealed class FakeBin : Core.Operations.IRecycleBin
{
    public List<string> Recycled { get; } = [];

    public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        foreach (var p in paths)
        {
            Recycled.Add(p);
            if (Directory.Exists(p))
            {
                Directory.Delete(p, recursive: true);
            }
            else
            {
                File.Delete(p);
            }
        }

        return Task.CompletedTask;
    }
}
