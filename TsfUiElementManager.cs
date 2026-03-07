using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace ConPtyTerminal;

internal sealed class TsfUiElementManager : ITfUIElementSink, IDisposable
{
    private const uint TfTmaeUiElementEnabledOnly = 0x00000004;

    private readonly HashSet<uint> _activeUiElementIds = [];
    private readonly Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    private ITfThreadMgrEx? _threadManager;
    private ITfSource? _source;
    private ITfUIElementMgr? _uiElementManager;
    private uint _sinkCookie;
    private bool _ownsActivation;
    private bool _publishScheduled;
    private TsfImeUiSnapshot _snapshot = TsfImeUiSnapshot.Empty;

    public event Action<TsfImeUiSnapshot>? SnapshotChanged;

    public static TsfUiElementManager? TryInitialize()
    {
        try
        {
            var manager = new TsfUiElementManager();
            return manager.Initialize() ? manager : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_source is not null && _sinkCookie != 0)
        {
            try
            {
                _source.UnadviseSink(_sinkCookie);
            }
            catch
            {
            }
        }

        _sinkCookie = 0;
        _source = null;
        _uiElementManager = null;

        if (_threadManager is not null && _ownsActivation)
        {
            try
            {
                _threadManager.Deactivate();
            }
            catch
            {
            }
        }

        _threadManager = null;
    }

    void ITfUIElementSink.BeginUIElement(uint uiElementId, out bool show)
    {
        show = false;
        _activeUiElementIds.Add(uiElementId);
        SchedulePublishSnapshot();
    }

    void ITfUIElementSink.UpdateUIElement(uint uiElementId)
    {
        _activeUiElementIds.Add(uiElementId);
        SchedulePublishSnapshot();
    }

    void ITfUIElementSink.EndUIElement(uint uiElementId)
    {
        _activeUiElementIds.Remove(uiElementId);
        SchedulePublishSnapshot();
    }

    private bool Initialize()
    {
        object? threadManagerObject;
        int getThreadMgrResult = TF_GetThreadMgr(out threadManagerObject);
        if (getThreadMgrResult >= 0 && threadManagerObject is ITfThreadMgrEx existingThreadManager)
        {
            _threadManager = existingThreadManager;
            EnsureUiElementOnlyMode(existingThreadManager);
        }
        else
        {
            int createThreadMgrResult = TF_CreateThreadMgr(out threadManagerObject);
            if (createThreadMgrResult < 0 || threadManagerObject is not ITfThreadMgrEx createdThreadManager)
            {
                return false;
            }

            _threadManager = createdThreadManager;
            createdThreadManager.ActivateEx(out _, TfTmaeUiElementEnabledOnly);
            _ownsActivation = true;
        }

        _source = _threadManager as ITfSource;
        _uiElementManager = _threadManager as ITfUIElementMgr;
        if (_source is null || _uiElementManager is null)
        {
            Dispose();
            return false;
        }

        _source.AdviseSink(typeof(ITfUIElementSink).GUID, this, out _sinkCookie);
        return true;
    }

    private void EnsureUiElementOnlyMode(ITfThreadMgrEx threadManager)
    {
        try
        {
            threadManager.GetActiveFlags(out uint activeFlags);
            if ((activeFlags & TfTmaeUiElementEnabledOnly) != 0)
            {
                return;
            }

            threadManager.Deactivate();
            threadManager.ActivateEx(out _, TfTmaeUiElementEnabledOnly);
            _ownsActivation = true;
        }
        catch
        {
        }
    }

    private void PublishSnapshot()
    {
        _publishScheduled = false;
        TsfImeUiSnapshot snapshot = BuildSnapshot();
        if (_snapshot.Equals(snapshot))
        {
            return;
        }

        _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
    }

    private void SchedulePublishSnapshot()
    {
        if (_publishScheduled)
        {
            return;
        }

        _publishScheduled = true;
        _ = _dispatcher.BeginInvoke(PublishSnapshot, DispatcherPriority.Input);
    }

    private TsfImeUiSnapshot BuildSnapshot()
    {
        if (_uiElementManager is null || _activeUiElementIds.Count == 0)
        {
            return TsfImeUiSnapshot.Empty;
        }

        string readingText = string.Empty;
        string[] candidateItems = [];
        int selectionIndex = -1;
        int pageStart = 0;
        List<uint> staleIds = [];

        foreach (uint uiElementId in _activeUiElementIds)
        {
            try
            {
                _uiElementManager.GetUIElement(uiElementId, out ITfUIElement uiElement);
                uiElement.Show(false);

                if (uiElement is ITfReadingInformationUIElement readingElement)
                {
                    readingElement.Show(false);
                    readingText = readingElement.GetString() ?? string.Empty;
                }

                if (uiElement is ITfCandidateListUIElement candidateElement)
                {
                    candidateElement.Show(false);
                    ReadCandidateSnapshot(candidateElement, out candidateItems, out selectionIndex, out pageStart);
                }
            }
            catch (COMException)
            {
                staleIds.Add(uiElementId);
            }
        }

        foreach (uint staleId in staleIds)
        {
            _activeUiElementIds.Remove(staleId);
        }

        return new TsfImeUiSnapshot(readingText, candidateItems, selectionIndex, pageStart);
    }

    private static void ReadCandidateSnapshot(
        ITfCandidateListUIElement candidateElement,
        out string[] candidateItems,
        out int selectionIndex,
        out int pageStart)
    {
        candidateItems = [];
        selectionIndex = -1;
        pageStart = 0;

        uint candidateCount = candidateElement.GetCount();
        if (candidateCount == 0)
        {
            return;
        }

        string[] allCandidates = new string[candidateCount];
        for (uint index = 0; index < candidateCount; index++)
        {
            allCandidates[index] = candidateElement.GetString(index) ?? string.Empty;
        }

        uint selectedIndex = candidateElement.GetSelection();
        uint currentPage = candidateElement.GetCurrentPage();

        uint[] pageIndices = new uint[candidateCount];
        candidateElement.GetPageIndex(pageIndices, (uint)pageIndices.Length, out uint pageCount);

        uint displayStart = 0;
        uint displayEnd = candidateCount;
        if (pageCount > 0 && currentPage < pageCount)
        {
            displayStart = pageIndices[currentPage];
            displayEnd = currentPage + 1 < pageCount ? pageIndices[currentPage + 1] : candidateCount;
        }

        if (displayStart >= candidateCount || displayEnd > candidateCount || displayEnd < displayStart)
        {
            displayStart = 0;
            displayEnd = candidateCount;
        }

        candidateItems = allCandidates[(int)displayStart..(int)displayEnd];
        pageStart = (int)displayStart;
        selectionIndex = selectedIndex >= displayStart && selectedIndex < displayEnd
            ? (int)(selectedIndex - displayStart)
            : -1;
    }

    [DllImport("msctf.dll")]
    private static extern int TF_CreateThreadMgr([MarshalAs(UnmanagedType.Interface)] out object? threadManager);

    [DllImport("msctf.dll")]
    private static extern int TF_GetThreadMgr([MarshalAs(UnmanagedType.Interface)] out object? threadManager);
}

internal sealed record TsfImeUiSnapshot(string ReadingText, string[] CandidateItems, int SelectionIndex, int PageStart)
{
    public static TsfImeUiSnapshot Empty { get; } = new(string.Empty, [], -1, 0);

    public bool Equals(TsfImeUiSnapshot? other)
    {
        return other is not null &&
            SelectionIndex == other.SelectionIndex &&
            PageStart == other.PageStart &&
            string.Equals(ReadingText, other.ReadingText, StringComparison.Ordinal) &&
            CandidateItems.AsSpan().SequenceEqual(other.CandidateItems);
    }

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(ReadingText, StringComparer.Ordinal);
        hash.Add(SelectionIndex);
        hash.Add(PageStart);
        foreach (string item in CandidateItems)
        {
            hash.Add(item, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}

[ComImport]
[Guid("BB08F7A9-607A-4384-8623-056892B64371")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfThreadMgr
{
    void Activate(out uint clientId);
    void Deactivate();
    void CreateDocumentMgr([MarshalAs(UnmanagedType.Interface)] out object? documentManager);
    void EnumDocumentMgrs([MarshalAs(UnmanagedType.Interface)] out object? enumDocumentManagers);
    void GetFocus([MarshalAs(UnmanagedType.Interface)] out object? focusedDocumentManager);
    void SetFocus([MarshalAs(UnmanagedType.Interface)] object? documentManager);
    void AssociateFocus(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] object? newDocumentManager, [MarshalAs(UnmanagedType.Interface)] out object? previousDocumentManager);
    void IsThreadFocus([MarshalAs(UnmanagedType.Bool)] out bool isThreadFocus);
    void GetFunctionProvider(in Guid classId, [MarshalAs(UnmanagedType.Interface)] out object? functionProvider);
    void EnumFunctionProviders([MarshalAs(UnmanagedType.Interface)] out object? enumFunctionProviders);
    void GetGlobalCompartment([MarshalAs(UnmanagedType.Interface)] out object? compartmentManager);
}

[ComImport]
[Guid("3E90ADE3-7594-4CB0-BB58-69628F5F458C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfThreadMgrEx : ITfThreadMgr
{
    new void Activate(out uint clientId);
    new void Deactivate();
    new void CreateDocumentMgr([MarshalAs(UnmanagedType.Interface)] out object? documentManager);
    new void EnumDocumentMgrs([MarshalAs(UnmanagedType.Interface)] out object? enumDocumentManagers);
    new void GetFocus([MarshalAs(UnmanagedType.Interface)] out object? focusedDocumentManager);
    new void SetFocus([MarshalAs(UnmanagedType.Interface)] object? documentManager);
    new void AssociateFocus(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] object? newDocumentManager, [MarshalAs(UnmanagedType.Interface)] out object? previousDocumentManager);
    new void IsThreadFocus([MarshalAs(UnmanagedType.Bool)] out bool isThreadFocus);
    new void GetFunctionProvider(in Guid classId, [MarshalAs(UnmanagedType.Interface)] out object? functionProvider);
    new void EnumFunctionProviders([MarshalAs(UnmanagedType.Interface)] out object? enumFunctionProviders);
    new void GetGlobalCompartment([MarshalAs(UnmanagedType.Interface)] out object? compartmentManager);
    void ActivateEx(out uint clientId, uint flags);
    void GetActiveFlags(out uint flags);
}

[ComImport]
[Guid("4EA48A35-60AE-446F-8FD6-E6A8D82459F7")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfSource
{
    void AdviseSink(in Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] object sink, out uint cookie);
    void UnadviseSink(uint cookie);
}

[ComImport]
[Guid("EA1EA135-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfUIElementMgr
{
    void BeginUIElement([MarshalAs(UnmanagedType.Interface)] ITfUIElement element, [MarshalAs(UnmanagedType.Bool)] ref bool show, out uint uiElementId);
    void UpdateUIElement(uint uiElementId);
    void EndUIElement(uint uiElementId);
    void GetUIElement(uint uiElementId, [MarshalAs(UnmanagedType.Interface)] out ITfUIElement element);
    void EnumUIElements([MarshalAs(UnmanagedType.Interface)] out object? enumUiElements);
}

[ComImport]
[Guid("EA1EA136-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfUIElementSink
{
    void BeginUIElement(uint uiElementId, [MarshalAs(UnmanagedType.Bool)] out bool show);
    void UpdateUIElement(uint uiElementId);
    void EndUIElement(uint uiElementId);
}

[ComImport]
[Guid("EA1EA137-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfUIElement
{
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetDescription();
    Guid GetGUID();
    void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    void IsShown([MarshalAs(UnmanagedType.Bool)] out bool show);
}

[ComImport]
[Guid("EA1EA138-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfCandidateListUIElement : ITfUIElement
{
    new string GetDescription();
    new Guid GetGUID();
    new void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    new void IsShown([MarshalAs(UnmanagedType.Bool)] out bool show);
    void GetUpdatedFlags(out uint updatedFlags);
    void GetDocumentMgr([MarshalAs(UnmanagedType.Interface)] out object? documentManager);
    uint GetCount();
    uint GetSelection();
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetString(uint index);
    void GetPageIndex([Out] uint[] pageIndex, uint size, out uint pageCount);
    void SetPageIndex([In] uint[] pageIndex, uint pageCount);
    uint GetCurrentPage();
}

[ComImport]
[Guid("EA1EA139-19DF-11D7-A6D2-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITfReadingInformationUIElement : ITfUIElement
{
    new string GetDescription();
    new Guid GetGUID();
    new void Show([MarshalAs(UnmanagedType.Bool)] bool show);
    new void IsShown([MarshalAs(UnmanagedType.Bool)] out bool show);
    void GetUpdatedFlags(out uint updatedFlags);
    void GetContext([MarshalAs(UnmanagedType.Interface)] out object? context);
    [return: MarshalAs(UnmanagedType.BStr)]
    string GetString();
    uint GetMaxReadingStringLength();
    uint GetErrorIndex();
    void IsVerticalOrderPreferred([MarshalAs(UnmanagedType.Bool)] out bool isVerticalPreferred);
}
