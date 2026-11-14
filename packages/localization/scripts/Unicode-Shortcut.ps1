# Shell links must use the Unicode interface explicitly. WScript.Shell's
# shortcut filename handling depends on the Windows ANSI code page.
if (-not ('AntigravityCompatibility.UnicodeShortcut' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
namespace AntigravityCompatibility {
  [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
  internal class ShellLinkClass {}
  [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
  internal interface IShellLinkW {
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length, IntPtr data, uint flags);
    void GetIDList(out IntPtr value);
    void SetIDList(IntPtr value);
    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length);
    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
    void GetHotkey(out short value);
    void SetHotkey(short value);
    void GetShowCmd(out int value);
    void SetShowCmd(int value);
    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, int length, out int index);
    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved);
    void Resolve(IntPtr window, uint flags);
    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
  }
  public sealed class UnicodeShortcut {
    readonly IShellLinkW link;
    readonly string filename;
    public UnicodeShortcut(string path) {
      filename = Path.GetFullPath(path);
      link = (IShellLinkW)new ShellLinkClass();
      if (File.Exists(filename)) ((IPersistFile)link).Load(filename, 0);
    }
    public string TargetPath {
      get { var value = new StringBuilder(32768); link.GetPath(value,value.Capacity,IntPtr.Zero,4); return value.ToString(); }
      set { link.SetPath(value); }
    }
    public string Arguments {
      get { var value = new StringBuilder(32768); link.GetArguments(value,value.Capacity); return value.ToString(); }
      set { link.SetArguments(value); }
    }
    public string WorkingDirectory {
      get { var value = new StringBuilder(32768); link.GetWorkingDirectory(value,value.Capacity); return value.ToString(); }
      set { link.SetWorkingDirectory(value); }
    }
    public string Description {
      get { var value = new StringBuilder(32768); link.GetDescription(value,value.Capacity); return value.ToString(); }
      set { link.SetDescription(value); }
    }
    public string IconLocation {
      get { var value = new StringBuilder(32768); int index; link.GetIconLocation(value,value.Capacity,out index); return value.ToString()+","+index; }
      set { int index = 0; int comma = value.LastIndexOf(','); if (comma >= 0 && Int32.TryParse(value.Substring(comma+1),out index)) link.SetIconLocation(value.Substring(0,comma),index); else link.SetIconLocation(value,0); }
    }
    public void Save() { ((IPersistFile)link).Save(filename,true); }
  }
}
'@
}
function New-UnicodeShortcut {
  param([Parameter(Mandatory)][string]$Path)
  return [AntigravityCompatibility.UnicodeShortcut]::new($Path)
}
