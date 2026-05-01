#pragma warning disable CA1401 // p/invokes should not be visible
#pragma warning disable CA2101 // marshaling for p/invoke string arguments
#pragma warning disable SYSLIB1054 // use LibraryImport instead of DllImport
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SfwPlayer.Services;

namespace SfwPlayer.Platform.MacOS;

internal static class AppleWebAuth
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor { public nuint Reserved, Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public IntPtr Isa;
        public int Flags, Reserved;
        public IntPtr Invoke; // function pointer stored as IntPtr
        public IntPtr Descriptor;
    }

    private static readonly unsafe BlockLiteral* _block;
    private static readonly unsafe BlockDescriptor* _desc;
    private static readonly IntPtr _blockPtr;
    private static readonly IntPtr _contextClass;
    private static volatile TaskCompletionSource<bool>? _tcs;
    private static volatile bool _succeeded;
    private static IntPtr _anchor;

    static unsafe AppleWebAuth()
    {
        if (!OperatingSystem.IsMacOS()) return;

        NativeLibrary.TryLoad(
            "/System/Library/Frameworks/AuthenticationServices.framework/AuthenticationServices",
            typeof(AppleWebAuth).Assembly, null, out _);

        IntPtr blockIsa = IntPtr.Zero;
        if (NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", typeof(AppleWebAuth).Assembly, null, out var sysLib))
            NativeLibrary.TryGetExport(sysLib, "_NSConcreteGlobalBlock", out blockIsa);

        _desc = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        _desc->Reserved = 0;
        _desc->Size = (nuint)sizeof(BlockLiteral);

        _block = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        _block->Isa = blockIsa;
        _block->Flags = 1 << 28; // BLOCK_IS_GLOBAL
        _block->Reserved = 0;
        _block->Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<BlockLiteral*, IntPtr, IntPtr, void>)&OnComplete;
        _block->Descriptor = (IntPtr)_desc;

        _blockPtr = (IntPtr)_block;

        // create ObjC class implementing ASWebAuthenticationPresentationContextProviding
        var nso = objc_getClass("NSObject");
        _contextClass = objc_allocateClassPair(nso, "SfwAuthContext", 0);
        class_addMethod(_contextClass,
            sel_registerName("presentationAnchorForWebAuthenticationSession:"),
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)&GetAnchor,
            "@24@0:8@16");
        objc_registerClassPair(_contextClass);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnComplete(BlockLiteral* block, IntPtr callbackUrl, IntPtr error)
    {
        _tcs?.TrySetResult(_succeeded);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr GetAnchor(IntPtr self, IntPtr sel, IntPtr session) => _anchor;

    // nsView is Avalonia's platform handle (NSView*); internally obtains NSWindow via [nsView window]
    internal static async Task<List<Cookie>?> SignInAsync(IntPtr nsView, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        _anchor = msg_ptr(nsView, sel_registerName("window"));
        if (_anchor == IntPtr.Zero) return null;
        return await SignInCoreAsync(ct);
    }

    // for testing: pass an NSWindow directly, bypassing the [nsView window] lookup
    internal static Task<List<Cookie>?> SignInWithAnchorAsync(IntPtr nsWindow, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS() || nsWindow == IntPtr.Zero) return Task.FromResult<List<Cookie>?>(null);
        _anchor = nsWindow;
        return SignInCoreAsync(ct);
    }

    private static async Task<List<Cookie>?> SignInCoreAsync(CancellationToken ct)
    {
        _succeeded = false;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tcs = tcs;

        // build NSURL for Google sign-in landing on YouTube
        var nsStrClass = objc_getClass("NSString");
        var nsUrlClass = objc_getClass("NSURL");
        var urlStr = msg_ptr_str(nsStrClass, sel_registerName("stringWithUTF8String:"),
            "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https://www.youtube.com/");
        var nsUrl = msg_ptr_p(nsUrlClass, sel_registerName("URLWithString:"), urlStr);

        // create and init ASWebAuthenticationSession
        var sessionClass = objc_getClass("ASWebAuthenticationSession");
        var session = msg_ptr(sessionClass, sel_registerName("alloc"));
        var scheme = msg_ptr_str(nsStrClass, sel_registerName("stringWithUTF8String:"), "sfwplayer-auth");
        session = msg_ptr_ppp(session,
            sel_registerName("initWithURL:callbackURLScheme:completionHandler:"),
            nsUrl, scheme, _blockPtr);

        // use Safari's shared cookie store so existing Google sign-in session is available
        msg_void_bool(session, sel_registerName("setPrefersEphemeralWebBrowserSession:"), false);

        // attach presentation context so the sheet anchors to our window
        var ctx = msg_ptr(msg_ptr(_contextClass, sel_registerName("alloc")), sel_registerName("init"));
        msg_void_p(session, sel_registerName("setPresentationContextProvider:"), ctx);

        msg_void(session, sel_registerName("start"));

        // poll Safari's cookie store for the YouTube SID cookie
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        _ = PollAsync(pollCts.Token);

        var ok = await tcs.Task;
        pollCts.Cancel();

        // cancel from the main thread (safe; no-op if already dismissed by user)
        msg_void(session, sel_registerName("cancel"));

        return ok ? BrowserCookieReader.TryReadSafariCookies() : null;
    }

    private static async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1500, ct); }
            catch (OperationCanceledException) { break; }

            var cookies = BrowserCookieReader.TryReadSafariCookies();
            if (cookies.Any(c => c.Name == "SID" && c.Domain.Contains("youtube") && c.Value.Length > 0))
            {
                _succeeded = true;
                _tcs?.TrySetResult(true);
                return;
            }
        }
        _tcs?.TrySetResult(false);
    }

    // ObjC runtime
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, nuint extra);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern void objc_registerClassPair(IntPtr cls);

    // objc_msgSend variants
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_p(IntPtr obj, IntPtr sel, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_ppp(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void msg_void(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void msg_void_p(IntPtr obj, IntPtr sel, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void msg_void_bool(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.U1)] bool a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_str(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.LPStr)] string s);
}
