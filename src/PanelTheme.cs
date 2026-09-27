using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tamago {
    // Keeps the WPF panel in step with the Windows app theme. The pet window has its own visuals.
    sealed class PanelTheme : IDisposable {
        const int SettingChange=0x001A,ThemeChanged=0x031A;
        readonly Window window;
        HwndSource source;
        bool disposed;
        internal bool IsDark { get; private set; }
        internal bool IsHighContrast { get; private set; }

        internal PanelTheme(Window panel) {
            window=panel;
            window.SourceInitialized+=SourceInitialized;
            window.Activated+=Activated;
            ApplySystemTheme();
        }
        void SourceInitialized(object sender,EventArgs args) {
            source=HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
            if(source!=null)source.AddHook(WindowMessage);
        }
        void Activated(object sender,EventArgs args) { ApplySystemTheme(); }
        IntPtr WindowMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
            if(message==SettingChange||message==ThemeChanged)ApplySystemTheme();
            return IntPtr.Zero;
        }
        static bool SystemDark() {
            try {
                using(RegistryKey key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) {
                    object value=key==null?null:key.GetValue("AppsUseLightTheme");
                    return value is int&&(int)value==0;
                }
            } catch(Exception error) {
                if(!(error is UnauthorizedAccessException)&&!(error is System.Security.SecurityException))throw;
                return false;
            }
        }
        internal void ApplySystemTheme() {
            if(!disposed)Apply(SystemDark(),SystemParameters.HighContrast);
        }
        static Brush Solid(string color) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)); }
        static void Put(ResourceDictionary resources,string name,Brush value) { resources[name]=value; }
        internal void ApplyForTesting(bool dark,bool highContrast) { Apply(dark,highContrast); }
        void Apply(bool dark,bool highContrast) {
            IsDark=dark;IsHighContrast=highContrast;
            ResourceDictionary r=window.Resources;
            if(highContrast) {
                Put(r,"WindowBackgroundBrush",SystemColors.WindowBrush);
                Put(r,"SidebarBackgroundBrush",SystemColors.ControlBrush);
                Put(r,"SurfaceBrush",SystemColors.WindowBrush);
                Put(r,"SurfaceAltBrush",SystemColors.ControlBrush);
                Put(r,"TextPrimaryBrush",SystemColors.WindowTextBrush);
                Put(r,"TextSecondaryBrush",SystemColors.WindowTextBrush);
                Put(r,"BorderBrush",SystemColors.ControlTextBrush);
                Put(r,"AccentBrush",SystemColors.HighlightBrush);
                Put(r,"AccentSoftBrush",SystemColors.HighlightBrush);
                Put(r,"AccentForegroundBrush",SystemColors.HighlightTextBrush);
                Put(r,"NavSelectedTextBrush",SystemColors.HighlightTextBrush);
                Put(r,"SelectedTextBrush",SystemColors.HighlightTextBrush);
                Put(r,"HoverBrush",SystemColors.HighlightBrush);
                Put(r,"SuccessBrush",SystemColors.WindowTextBrush);
                Put(r,"WarningBrush",SystemColors.WindowTextBrush);
                Put(r,"DangerBrush",SystemColors.WindowTextBrush);
                return;
            }
            Put(r,"WindowBackgroundBrush",Solid(dark?"#1C1C1E":"#F5F5F7"));
            Put(r,"SidebarBackgroundBrush",Solid(dark?"#242427":"#EEEEF2"));
            Put(r,"SurfaceBrush",Solid(dark?"#2C2C30":"#FFFFFF"));
            Put(r,"SurfaceAltBrush",Solid(dark?"#37373B":"#F0F0F4"));
            Put(r,"TextPrimaryBrush",Solid(dark?"#F5F5F7":"#25252A"));
            Put(r,"TextSecondaryBrush",Solid(dark?"#B4B4BC":"#72727B"));
            Put(r,"BorderBrush",Solid(dark?"#47474C":"#DEDEE4"));
            Put(r,"AccentBrush",Solid(dark?"#0A84FF":"#007AFF"));
            Put(r,"AccentSoftBrush",Solid(dark?"#193A58":"#E5F0FF"));
            Put(r,"AccentForegroundBrush",Solid("#FFFFFF"));
            Put(r,"NavSelectedTextBrush",Solid(dark?"#0A84FF":"#007AFF"));
            Put(r,"SelectedTextBrush",Solid(dark?"#F5F5F7":"#25252A"));
            Put(r,"HoverBrush",Solid(dark?"#414147":"#E8E8EE"));
            Put(r,"SuccessBrush",Solid(dark?"#30D158":"#248A3D"));
            Put(r,"WarningBrush",Solid(dark?"#FFD60A":"#B36A00"));
            Put(r,"DangerBrush",Solid(dark?"#FF6961":"#D93025"));
        }
        public void Dispose() {
            if(disposed)return;
            disposed=true;
            window.SourceInitialized-=SourceInitialized;
            window.Activated-=Activated;
            if(source!=null)source.RemoveHook(WindowMessage);
        }
    }
}
