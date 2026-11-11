// Read-only package/window/Shell evidence; invoked only by the Sandbox guest test.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

internal static class TaskbarIconProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern int GetApplicationUserModelId(IntPtr process, ref uint count, StringBuilder value);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern int GetPackageFullName(IntPtr process, ref uint count, StringBuilder value);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, UIntPtr parameter, IntPtr data, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, string file, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("MRM.dll", CharSet=CharSet.Unicode)] private static extern int MrmCreateResourceManager(string pri, out IntPtr manager);
    [DllImport("MRM.dll")] private static extern int MrmCreateResourceContext(IntPtr manager, out IntPtr context);
    [DllImport("MRM.dll", CharSet=CharSet.Unicode)] private static extern int MrmSetQualifier(IntPtr context, string name, string value);
    [DllImport("MRM.dll", CharSet=CharSet.Unicode)] private static extern int MrmLoadStringResource(IntPtr manager, IntPtr context, IntPtr map, string id, out IntPtr value);
    [DllImport("MRM.dll")] private static extern void MrmFreeResource(IntPtr value);
    [DllImport("MRM.dll")] private static extern void MrmDestroyResourceContext(IntPtr context);
    [DllImport("MRM.dll")] private static extern void MrmDestroyResourceManager(IntPtr manager);

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void HResult(int value) { if (value < 0) Marshal.ThrowExceptionForHR(value); }
    private static string Hash(string file)
    {
        using (SHA256 algorithm=SHA256.Create()) using (Stream data=File.OpenRead(file))
            return BitConverter.ToString(algorithm.ComputeHash(data)).Replace("-", "");
    }
    private static string Identity(Process process, bool package, out int status)
    {
        uint count=0;
        status=package ? GetPackageFullName(process.Handle, ref count, null) : GetApplicationUserModelId(process.Handle, ref count, null);
        if (status != 122) return null;
        StringBuilder value=new StringBuilder((int)count);
        status=package ? GetPackageFullName(process.Handle, ref count, value) : GetApplicationUserModelId(process.Handle, ref count, value);
        return status == 0 ? value.ToString() : null;
    }
    private static bool PixelsEqual(Bitmap first, Bitmap second)
    {
        if (first.Size != second.Size) return false;
        for (int y=0; y<first.Height; y++) for (int x=0; x<first.Width; x++)
            if (first.GetPixel(x,y).ToArgb() != second.GetPixel(x,y).ToArgb()) return false;
        return true;
    }
    private static object WindowIcon(IntPtr hwnd, string source, string output, bool small)
    {
        UIntPtr result;
        Check(SendMessageTimeout(hwnd, 0x007f, new UIntPtr(small ? 0u : 1u), IntPtr.Zero, 2, 1000, out result) != IntPtr.Zero, "WM_GETICON timed out.");
        IntPtr handle=new IntPtr(unchecked((long)result.ToUInt64()));
        if (handle == IntPtr.Zero) handle=GetClassLongPtr(hwnd, small ? -34 : -14);
        Check(handle != IntPtr.Zero, "Window icon is missing.");
        using (Icon icon=(Icon)Icon.FromHandle(handle).Clone()) using (Bitmap bitmap=icon.ToBitmap())
        {
            bitmap.Save(output, ImageFormat.Png);
            IntPtr expected=LoadImage(IntPtr.Zero, source, 1, bitmap.Width, bitmap.Height, 0x10);
            Check(expected != IntPtr.Zero, "Could not load the installed ICO.");
            bool equal;
            try { using (Icon reference=(Icon)Icon.FromHandle(expected).Clone()) using (Bitmap referenceBitmap=reference.ToBitmap()) equal=PixelsEqual(bitmap, referenceBitmap); }
            finally { DestroyIcon(expected); }
            Check(equal, "The window HICON differs from the installed ICO at the same native size.");
            return new { width=bitmap.Width, height=bitmap.Height, matchesInstalledIco=equal, png=output, sha256=Hash(output) };
        }
    }
    private static object Taskbar(string output)
    {
        IntPtr old=SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            Rect bounds=new Rect();
            IntPtr tray=FindWindow("Shell_TrayWnd", null);
            Check(tray != IntPtr.Zero && GetWindowRect(tray, out bounds), "The Sandbox taskbar could not be located.");
            int width=bounds.Right-bounds.Left, height=bounds.Bottom-bounds.Top;
            Check(width > 0 && height > 0 && width <= 16384 && height <= 16384 && (long)width*height <= 16*1024*1024, "Invalid taskbar bounds.");
            DwmFlush();
            using (Bitmap image=new Bitmap(width,height,PixelFormat.Format32bppArgb))
            using (Graphics graphics=Graphics.FromImage(image))
            {
                graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,new Size(width,height),CopyPixelOperation.SourceCopy);
                int first=image.GetPixel(0,0).ToArgb();
                bool varied=false;
                for (int y=0; y<height && !varied; y+=4) for (int x=0; x<width; x+=4)
                    if (image.GetPixel(x,y).ToArgb() != first) { varied=true; break; }
                Check(varied,"Taskbar capture returned a uniform surface; visual evidence is unavailable.");
                image.Save(output,ImageFormat.Png);
            }
            return new { left=bounds.Left, top=bounds.Top, width=width, height=height, png=output, sha256=Hash(output), scope="Unedited taskbar pixels from the disposable Sandbox only; visual acceptance is separate." };
        }
        finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }
    private static object ResolveShellIcons(string root)
    {
        // The parent copies only these two verified payload DLLs beside this
        // short-lived helper. It cannot keep installed files locked at uninstall.
        Check(LoadLibraryEx(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"MRM.dll"),IntPtr.Zero,0x1100) != IntPtr.Zero,"Could not load the controlled MRT helper copy.");
        IntPtr manager=IntPtr.Zero, context=IntPtr.Zero;
        List<object> selections=new List<object>();
        try
        {
            HResult(MrmCreateResourceManager(Path.Combine(root,"resources.pri"),out manager));
            HResult(MrmCreateResourceContext(manager,out context));
            HResult(MrmSetQualifier(context,"Scale","100"));
            HResult(MrmSetQualifier(context,"Contrast","standard"));
            foreach (int size in new int[] {16,20,24,30,32,36,40,48,60,64,72,80,96,256})
            foreach (string form in new string[] {"unplated","lightunplated"})
            {
                HResult(MrmSetQualifier(context,"TargetSize",size.ToString()));
                HResult(MrmSetQualifier(context,"AlternateForm",form));
                IntPtr value=IntPtr.Zero;
                string resolved;
                try { HResult(MrmLoadStringResource(manager,context,IntPtr.Zero,"Files/Assets/Square44x44Logo.png",out value)); resolved=Marshal.PtrToStringUni(value); }
                finally { if (value != IntPtr.Zero) MrmFreeResource(value); }
                string expected=Path.GetFullPath(Path.Combine(root,"Assets/Square44x44Logo.targetsize-"+size+"_altform-"+form+".png"));
                string actual=Path.GetFullPath(Path.IsPathRooted(resolved) ? resolved : Path.Combine(root,resolved));
                Check(String.Equals(expected,actual,StringComparison.OrdinalIgnoreCase),"MRT did not select the requested unplated Shell asset: "+actual);
                selections.Add(new { targetSize=size, alternateForm=form, path=actual, sha256=Hash(actual) });
            }
        }
        finally { if (context != IntPtr.Zero) MrmDestroyResourceContext(context); if (manager != IntPtr.Zero) MrmDestroyResourceManager(manager); }
        return selections;
    }
    [STAThread]
    private static int Main(string[] args)
    {
        string output=args.Length > 3 ? args[3] : null;
        Dictionary<string,object> report=new Dictionary<string,object>();
        try
        {
            Check(String.Equals(Environment.UserName,"WDAGUtilityAccount",StringComparison.OrdinalIgnoreCase) && File.Exists(@"C:\ClearCastInput\configuration.json"),"This probe runs only inside the configured disposable Sandbox.");
            Check(args.Length == 5,"Expected PID, HWND, application root, output prefix and expected AUMID.");
            string root=Path.GetFullPath(args[2]);
            using (Process process=Process.GetProcessById(Int32.Parse(args[0])))
            {
                Check(String.Equals(Path.GetFullPath(process.MainModule.FileName),Path.Combine(root,"ClearCast.exe"),StringComparison.OrdinalIgnoreCase),"The probe target is not the installed test app.");
                IntPtr hwnd=new IntPtr(Int64.Parse(args[1]));
                Check(hwnd == process.MainWindowHandle,"The target HWND is not the owned application window.");
                int appIdStatus, packageStatus;
                string appId=Identity(process,false,out appIdStatus), package=Identity(process,true,out packageStatus);
                report["applicationUserModelId"]=appId; report["applicationUserModelIdStatus"]=appIdStatus;
                report["packageFullName"]=package; report["packageStatus"]=packageStatus;
                if (args[4] != "-")
                {
                    Check(String.Equals(appId,args[4],StringComparison.Ordinal),"The Store process does not have its expected package AUMID.");
                    Check(packageStatus == 0 && !String.IsNullOrEmpty(package),"The Store process has no package identity.");
                    report["shellSelections"]=ResolveShellIcons(root);
                }
                string ico=Path.Combine(root,"Assets/ClearCast.ico");
                report["nativeLarge"]=WindowIcon(hwnd,ico,output+"-large.png",false);
                report["nativeSmall"]=WindowIcon(hwnd,ico,output+"-small.png",true);
                report["taskbar"]=Taskbar(output+"-taskbar.png");
            }
            report["passed"]=true;
        }
        catch (Exception error) { report["passed"]=false; report["error"]=error.ToString(); }
        if (output != null) File.WriteAllText(output+".json",new JavaScriptSerializer().Serialize(report));
        return report.ContainsKey("passed") && (bool)report["passed"] ? 0 : 1;
    }
}
