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
        public IntPtr Invoke;
        public IntPtr Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double X, Y, W, H; }

    // cookie-reading block for WKHTTPCookieStore.getAllCookies:
    private static readonly unsafe BlockLiteral* _cookieBlock;
    private static readonly unsafe BlockDescriptor* _cookieDesc;
    private static readonly IntPtr _cookieBlockPtr;

    private static readonly unsafe BlockLiteral* _noopBlock;
    private static readonly unsafe BlockDescriptor* _noopDesc;
    private static readonly IntPtr _noopBlockPtr;

    // kept for test: SfwAuthContext registered so tests can assert ObjC class registration
    private static readonly IntPtr _contextClass;
    // SfwWebDelegate implements WKNavigationDelegate + NSWindowDelegate
    private static readonly IntPtr _delegateClass;
    // dispatch_get_main_queue() is a C macro for &_dispatch_main_q — resolve at startup
    private static readonly IntPtr _mainQueue;

    private static volatile TaskCompletionSource<bool>? _tcs;
    private static volatile TaskCompletionSource<List<Cookie>>? _cookieTcs;
    private static volatile bool _succeeded;
    // predicate for OnNavFinished: set before each navigation
    private static volatile Func<string, bool>? _completionPredicate;

    internal const string GoogleSignInUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https://www.youtube.com/";

    static unsafe AppleWebAuth()
    {
        if (!OperatingSystem.IsMacOS()) return;

        NativeLibrary.TryLoad(
            "/System/Library/Frameworks/WebKit.framework/WebKit",
            typeof(AppleWebAuth).Assembly, null, out _);

        IntPtr blockIsa = IntPtr.Zero;
        if (NativeLibrary.TryLoad("/usr/lib/libSystem.B.dylib", typeof(AppleWebAuth).Assembly, null, out var sysLib))
            NativeLibrary.TryGetExport(sysLib, "_NSConcreteGlobalBlock", out blockIsa);

        // dispatch_get_main_queue() is a C macro expanding to &_dispatch_main_q
        if (NativeLibrary.TryLoad("/usr/lib/system/libdispatch.dylib", typeof(AppleWebAuth).Assembly, null, out var dispatchLib))
            NativeLibrary.TryGetExport(dispatchLib, "_dispatch_main_q", out _mainQueue);

        _cookieDesc = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        _cookieDesc->Reserved = 0;
        _cookieDesc->Size = (nuint)sizeof(BlockLiteral);

        _cookieBlock = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        _cookieBlock->Isa = blockIsa;
        _cookieBlock->Flags = 1 << 28; // BLOCK_IS_GLOBAL
        _cookieBlock->Reserved = 0;
        _cookieBlock->Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<BlockLiteral*, IntPtr, void>)&OnGetCookies;
        _cookieBlock->Descriptor = (IntPtr)_cookieDesc;
        _cookieBlockPtr = (IntPtr)_cookieBlock;

        _noopDesc = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        _noopDesc->Reserved = 0;
        _noopDesc->Size = (nuint)sizeof(BlockLiteral);

        _noopBlock = (BlockLiteral*)NativeMemory.Alloc((nuint)sizeof(BlockLiteral));
        _noopBlock->Isa = blockIsa;
        _noopBlock->Flags = 1 << 28; // BLOCK_IS_GLOBAL
        _noopBlock->Reserved = 0;
        _noopBlock->Invoke = (IntPtr)(delegate* unmanaged[Cdecl]<BlockLiteral*, void>)&NoopBlockInvoke;
        _noopBlock->Descriptor = (IntPtr)_noopDesc;
        _noopBlockPtr = (IntPtr)_noopBlock;

        var nso = objc_getClass("NSObject");

        // SfwAuthContext: kept so existing tests can verify ObjC class registration
        _contextClass = objc_allocateClassPair(nso, "SfwAuthContext", 0);
        class_addMethod(_contextClass,
            sel_registerName("presentationAnchorForWebAuthenticationSession:"),
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr>)&GetAnchor,
            "@24@0:8@16");
        objc_registerClassPair(_contextClass);

        // SfwWebDelegate: WKNavigationDelegate + NSWindowDelegate
        _delegateClass = objc_allocateClassPair(nso, "SfwWebDelegate", 0);
        class_addMethod(_delegateClass,
            sel_registerName("webView:didFinishNavigation:"),
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnNavFinished,
            "v32@0:8@16@24");
        class_addMethod(_delegateClass,
            sel_registerName("windowWillClose:"),
            (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnWindowClose,
            "v24@0:8@16");
        objc_registerClassPair(_delegateClass);
    }

    // legacy: kept so test assertions on SfwAuthContext registration still pass
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static IntPtr GetAnchor(IntPtr self, IntPtr sel, IntPtr session) => IntPtr.Zero;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNavFinished(IntPtr self, IntPtr sel, IntPtr webView, IntPtr navigation)
    {
        var urlObj = msg_ptr(webView, sel_registerName("URL"));
        if (urlObj == IntPtr.Zero) return;
        var absStr = msg_ptr(urlObj, sel_registerName("absoluteString"));
        if (absStr == IntPtr.Zero) return;
        var url = Marshal.PtrToStringUTF8(msg_ptr(absStr, sel_registerName("UTF8String")));
        if (url != null && _completionPredicate?.Invoke(url) == true)
        {
            _succeeded = true;
            _tcs?.TrySetResult(true);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWindowClose(IntPtr self, IntPtr sel, IntPtr notification)
    {
        // user closed the browser window — resolve with whatever state we're in
        _tcs?.TrySetResult(_succeeded);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DoOrderOut(IntPtr window)
    {
        msg_void_p(window, sel_registerName("orderOut:"), IntPtr.Zero);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnGetCookies(BlockLiteral* block, IntPtr nsArray)
    {
        _cookieTcs?.TrySetResult(ParseNsArray(nsArray));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void NoopBlockInvoke(BlockLiteral* block) { }

    // clears the shared WKWebsiteDataStore so the next sign-in shows a fresh Google login
    internal static void ClearWebKitSession()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var dataStore = msg_ptr(objc_getClass("WKWebsiteDataStore"), sel_registerName("defaultDataStore"));
        var dataTypes = msg_ptr(objc_getClass("WKWebsiteDataStore"), sel_registerName("allWebsiteDataTypes"));
        var distantPast = msg_ptr(objc_getClass("NSDate"), sel_registerName("distantPast"));
        msg_void_p_p_p(dataStore, sel_registerName("removeDataOfTypes:modifiedSince:completionHandler:"),
            dataTypes, distantPast, _noopBlockPtr);
    }

    // nsView is Avalonia's platform handle (NSView*); obtains NSWindow via [nsView window]
    internal static async Task<List<Cookie>?> SignInAsync(IntPtr nsView, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var nsWindow = msg_ptr(nsView, sel_registerName("window"));
        if (nsWindow == IntPtr.Zero) return null;
        return await SignInCoreAsync(ct);
    }

    // embeds a WKWebView directly into the given nsView's NSWindow — no separate auth window
    internal static async Task<List<Cookie>?> SignInInWindowAsync(IntPtr nsView, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var nsWindow = msg_ptr(nsView, sel_registerName("window"));
        if (nsWindow == IntPtr.Zero) return null;

        _completionPredicate = url =>
            url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase);
        return await NavigateInWindowAsync(nsWindow, GoogleSignInUrl, ct);
    }

    private static async Task<List<Cookie>?> NavigateInWindowAsync(IntPtr nsWindow, string url, CancellationToken ct)
    {
        _succeeded = false;
        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cookieTcs = null;

        var contentView = msg_ptr(nsWindow, sel_registerName("contentView"));
        var bounds = msg_nsrect(contentView, sel_registerName("bounds"));

        var wkConfig = msg_ptr(objc_getClass("WKWebViewConfiguration"), sel_registerName("new"));
        var webView = msg_ptr_nsrect_p(
            msg_ptr(objc_getClass("WKWebView"), sel_registerName("alloc")),
            sel_registerName("initWithFrame:configuration:"),
            bounds, wkConfig);

        // NSViewWidthSizable | NSViewHeightSizable = 18
        msg_void_nuint(webView, sel_registerName("setAutoresizingMask:"), 18);
        msg_void_p(contentView, sel_registerName("addSubview:"), webView);

        var del = msg_ptr(msg_ptr(_delegateClass, sel_registerName("alloc")), sel_registerName("init"));
        msg_void_p(webView, sel_registerName("setNavigationDelegate:"), del);

        var nsStrClass = objc_getClass("NSString");
        var nsUrlClass = objc_getClass("NSURL");
        var urlStr = msg_ptr_str(nsStrClass, sel_registerName("stringWithUTF8String:"), url);
        var nsUrl = msg_ptr_p(nsUrlClass, sel_registerName("URLWithString:"), urlStr);
        var request = msg_ptr_p(objc_getClass("NSURLRequest"), sel_registerName("requestWithURL:"), nsUrl);
        msg_ptr_p(webView, sel_registerName("loadRequest:"), request);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var reg = combined.Token.Register(() => _tcs?.TrySetResult(false));
        await _tcs.Task;

        msg_void(webView, sel_registerName("removeFromSuperview"));

        if (!_succeeded) return null;

        _cookieTcs = new TaskCompletionSource<List<Cookie>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataStore = msg_ptr(objc_getClass("WKWebsiteDataStore"), sel_registerName("defaultDataStore"));
        var cookieStore = msg_ptr(dataStore, sel_registerName("httpCookieStore"));
        msg_void_p(cookieStore, sel_registerName("getAllCookies:"), _cookieBlockPtr);
        return await _cookieTcs.Task;
    }

    // for testing: pass an NSWindow directly
    internal static Task<List<Cookie>?> SignInWithAnchorAsync(IntPtr nsWindow, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS() || nsWindow == IntPtr.Zero) return Task.FromResult<List<Cookie>?>(null);
        return SignInCoreAsync(ct);
    }

    // for testing: navigate to any URL and return cookies once done(url) returns true
    // returns null immediately on non-macOS or background threads (NSWindow requires main thread)
    internal static Task<List<Cookie>?> NavigateAndExtractAsync(string url, Func<string, bool> done, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS() || PthreadMainNp() == 0) return Task.FromResult<List<Cookie>?>(null);
        _completionPredicate = done;
        return NavigateCoreAsync(url, ct);
    }

    private static Task<List<Cookie>?> SignInCoreAsync(CancellationToken ct)
    {
        _completionPredicate = url =>
            url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase);
        return NavigateCoreAsync(GoogleSignInUrl, ct);
    }

    private static unsafe IntPtr DoOrderOutPtr() =>
        (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, void>)&DoOrderOut;

    private static async Task<List<Cookie>?> NavigateCoreAsync(string url, CancellationToken ct)
    {
        _succeeded = false;
        _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cookieTcs = null;

        var (webView, webWindow) = CreateWebViewWindow();

        var nsStrClass = objc_getClass("NSString");
        var nsUrlClass = objc_getClass("NSURL");
        var urlStr = msg_ptr_str(nsStrClass, sel_registerName("stringWithUTF8String:"), url);
        var nsUrl = msg_ptr_p(nsUrlClass, sel_registerName("URLWithString:"), urlStr);
        var request = msg_ptr_p(objc_getClass("NSURLRequest"), sel_registerName("requestWithURL:"), nsUrl);
        msg_ptr_p(webView, sel_registerName("loadRequest:"), request);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var combined = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        using var reg = combined.Token.Register(() => _tcs?.TrySetResult(false));
        await _tcs.Task;

        // orderOut: must run on main queue; we may be on a thread pool thread here
        dispatch_async_f(_mainQueue, webWindow, DoOrderOutPtr());

        if (!_succeeded) return null;

        // read cookies from WKWebsiteDataStore — in our process, always accessible
        _cookieTcs = new TaskCompletionSource<List<Cookie>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dataStore = msg_ptr(objc_getClass("WKWebsiteDataStore"), sel_registerName("defaultDataStore"));
        var cookieStore = msg_ptr(dataStore, sel_registerName("httpCookieStore"));
        msg_void_p(cookieStore, sel_registerName("getAllCookies:"), _cookieBlockPtr);
        return await _cookieTcs.Task;
    }

    private static (IntPtr webView, IntPtr webWindow) CreateWebViewWindow()
    {
        var rect = new NSRect { X = 150, Y = 100, W = 960, H = 720 };
        var window = msg_ptr_nsrect_nuint_nuint_bool(
            msg_ptr(objc_getClass("NSWindow"), sel_registerName("alloc")),
            sel_registerName("initWithContentRect:styleMask:backing:defer:"),
            rect, 7 /* titled|closable|miniaturizable */, 2 /* NSBackingStoreBuffered */, false);

        var title = msg_ptr_str(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"),
            "Sign in to YouTube — close when done");
        msg_void_p(window, sel_registerName("setTitle:"), title);
        msg_void_p(window, sel_registerName("makeKeyAndOrderFront:"), IntPtr.Zero);

        var contentView = msg_ptr(window, sel_registerName("contentView"));
        var bounds = msg_nsrect(contentView, sel_registerName("bounds"));

        var wkConfig = msg_ptr(objc_getClass("WKWebViewConfiguration"), sel_registerName("new"));
        var webView = msg_ptr_nsrect_p(
            msg_ptr(objc_getClass("WKWebView"), sel_registerName("alloc")),
            sel_registerName("initWithFrame:configuration:"),
            bounds, wkConfig);

        // NSViewWidthSizable | NSViewHeightSizable = 18
        msg_void_nuint(webView, sel_registerName("setAutoresizingMask:"), 18);
        msg_void_p(contentView, sel_registerName("addSubview:"), webView);

        var del = msg_ptr(msg_ptr(_delegateClass, sel_registerName("alloc")), sel_registerName("init"));
        msg_void_p(webView, sel_registerName("setNavigationDelegate:"), del);
        msg_void_p(window, sel_registerName("setDelegate:"), del);

        return (webView, window);
    }

    private static List<Cookie> ParseNsArray(IntPtr nsArray)
    {
        if (nsArray == IntPtr.Zero) return [];
        var count = msg_nuint(nsArray, sel_registerName("count"));

        var selObjAtIndex = sel_registerName("objectAtIndex:");
        var selName = sel_registerName("name");
        var selValue = sel_registerName("value");
        var selDomain = sel_registerName("domain");
        var selPath = sel_registerName("path");
        var selSecure = sel_registerName("isSecure");
        var selHttpOnly = sel_registerName("isHTTPOnly");
        var selExpires = sel_registerName("expiresDate");
        var selUtf8 = sel_registerName("UTF8String");
        var selT1970 = sel_registerName("timeIntervalSince1970");

        var result = new List<Cookie>();
        for (nuint i = 0; i < count; i++)
        {
            var c = msg_ptr_nuint(nsArray, selObjAtIndex, i);
            if (c == IntPtr.Zero) continue;

            var name = NsStr(msg_ptr(c, selName), selUtf8);
            var domain = NsStr(msg_ptr(c, selDomain), selUtf8);
            if (name.Length == 0 || domain.Length == 0) continue;

            var value = NsStr(msg_ptr(c, selValue), selUtf8);
            var path = NsStr(msg_ptr(c, selPath), selUtf8);
            if (path.Length == 0) path = "/";

            var cookie = new Cookie(name, value, path, domain)
            {
                Secure = msg_bool(c, selSecure),
                HttpOnly = msg_bool(c, selHttpOnly),
            };

            var dateObj = msg_ptr(c, selExpires);
            if (dateObj != IntPtr.Zero)
            {
                var t = msg_double(dateObj, selT1970);
                if (t > 0)
                {
                    var exp = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t);
                    if (exp > DateTime.UtcNow) cookie.Expires = exp;
                }
            }

            result.Add(cookie);
        }
        return result;
    }

    private static string NsStr(IntPtr nsStr, IntPtr selUtf8)
    {
        if (nsStr == IntPtr.Zero) return "";
        var ptr = msg_ptr(nsStr, selUtf8);
        return ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";
    }

    // ObjC runtime
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern IntPtr objc_allocateClassPair(IntPtr super, string name, nuint extra);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport("/usr/lib/libobjc.dylib")] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "pthread_main_np")] private static extern int PthreadMainNp();

    // objc_msgSend variants
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void msg_void(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_p(IntPtr obj, IntPtr sel, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void msg_void_p(IntPtr obj, IntPtr sel, IntPtr a);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_str(IntPtr obj, IntPtr sel, [MarshalAs(UnmanagedType.LPStr)] string s);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint msg_nuint(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_nuint(IntPtr obj, IntPtr sel, nuint idx);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool msg_bool(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern double msg_double(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern NSRect msg_nsrect(IntPtr obj, IntPtr sel);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_nsrect_p(IntPtr obj, IntPtr sel, NSRect frame, IntPtr config);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr msg_ptr_nsrect_nuint_nuint_bool(IntPtr obj, IntPtr sel, NSRect frame, nuint styleMask, nuint backing, bool defer);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void msg_void_nuint(IntPtr obj, IntPtr sel, nuint arg);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void msg_void_p_p_p(IntPtr obj, IntPtr sel, IntPtr a, IntPtr b, IntPtr c);

    [DllImport("/usr/lib/system/libdispatch.dylib")] private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);
}
