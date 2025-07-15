using Ink_Canvas.Helpers;
using iNKORE.UI.WPF.Modern;
using Microsoft.Office.Core;
using Microsoft.Office.Interop.PowerPoint;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Application = System.Windows.Application;
using File = System.IO.File;
using MessageBox = System.Windows.MessageBox;

namespace Ink_Canvas {
    public partial class MainWindow : Window {
        public static Microsoft.Office.Interop.PowerPoint.Application pptApplication = null;
        public static Presentation presentation = null;
        public static Slides slides = null;
        public static Slide slide = null;
        public static int slidescount = 0;

        private void BtnCheckPPT_Click(object sender, RoutedEventArgs e) {
            try {
                if (pptApplication != null) {
                    //获得演示文稿对象
                    try {
                        presentation = pptApplication.ActivePresentation;
                    }
                    catch (COMException) {
                        // ActivePresentation 可能因只读等原因抛异常，遍历 Presentations
                        presentation = null;
                        foreach (Presentation pres in pptApplication.Presentations) {
                            try {
                                if (pres.ReadOnly == MsoTriState.msoFalse) {
                                    presentation = pres;
                                    break;
                                }
                            } catch { }
                        }
                        // 如果没有可编辑的，选择第一个只读的
                        if (presentation == null && pptApplication.Presentations.Count > 0)
                            presentation = pptApplication.Presentations[1];
                    }
                    pptApplication.SlideShowBegin += PptApplication_SlideShowBegin;
                    pptApplication.SlideShowNextSlide += PptApplication_SlideShowNextSlide;
                    pptApplication.SlideShowEnd += PptApplication_SlideShowEnd;
                    // 获得幻灯片对象集合
                    slides = presentation.Slides;
                    // 获得幻灯片的数量
                    slidescount = slides.Count;
                    memoryStreams = new MemoryStream[slidescount + 2];
                    // 获得当前选中的幻灯片
                    try {
                        slide = slides[pptApplication.ActiveWindow.Selection.SlideRange.SlideNumber];
                    }
                    catch {
                        try {
                            slide = pptApplication.SlideShowWindows[1].View.Slide;
                        } catch { }
                    }
                }
                if (pptApplication == null) throw new Exception();
                StackPanelPPTControls.Visibility = Visibility.Visible;
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                StackPanelPPTControls.Visibility = Visibility.Collapsed;
                LeftBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                RightBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                LeftSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                RightSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                MessageBox.Show("未找到幻灯片");
            }
        }

        private void ToggleSwitchSupportWPS_Toggled(object sender, RoutedEventArgs e) {
            if (!isLoaded) return;

            Settings.PowerPointSettings.IsSupportWPS = ToggleSwitchSupportWPS.IsOn;
            SaveSettingsToFile();
            
            // 重置PowerPoint/WPS实例状态
            ResetPresentationObjects();
            isPowerPointInitialized = false;
        }
        
        /// <summary>
        /// 重置所有演示相关的COM对象
        /// </summary>
        private void ResetPresentationObjects()
        {
            try 
            {
                // 清理对象引用
                if (pptApplication != null)
                {
                    try 
                    {
                        // 尝试解除事件绑定
                        pptApplication.PresentationOpen -= PptApplication_PresentationOpen;
                        pptApplication.PresentationClose -= PptApplication_PresentationClose;
                        pptApplication.SlideShowBegin -= PptApplication_SlideShowBegin;
                        pptApplication.SlideShowNextSlide -= PptApplication_SlideShowNextSlide;
                        pptApplication.SlideShowEnd -= PptApplication_SlideShowEnd;
                    }
                    catch { }
                    
                    try { Marshal.ReleaseComObject(pptApplication); } catch { }
                    pptApplication = null;
                }
                
                if (presentation != null)
                {
                    try { Marshal.ReleaseComObject(presentation); } catch { }
                    presentation = null;
                }
                
                if (slides != null)
                {
                    try { Marshal.ReleaseComObject(slides); } catch { }
                    slides = null;
                }
                
                slide = null;
                
                // 强制GC回收
                GC.Collect();
                GC.WaitForPendingFinalizers();
                
                LogHelper.WriteLogToFile("成功重置所有演示对象", LogHelper.LogType.Info);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"重置演示对象时出错: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        private static bool isWPSSupportOn => Settings.PowerPointSettings.IsSupportWPS;

        public static bool IsShowingRestoreHiddenSlidesWindow = false;
        private static bool IsShowingAutoplaySlidesWindow = false;
        private bool isPowerPointInitialized = false;
        private bool isWPSMode = false;
        
        /// <summary>
        /// 获取所有可能的WPS进程名
        /// </summary>
        private string[] GetPossibleWPSProcessNames()
        {
            return new[] { "wpp", "wppmain", "wps", "et" };
        }
        
        /// <summary>
        /// 检查WPS进程是否正在运行
        /// </summary>
        /// <param name="writeLog">是否记录日志，默认为true</param>
        private bool IsWPSRunning(bool writeLog = true)
        {
            foreach (var processName in GetPossibleWPSProcessNames())
            {
                var processes = Process.GetProcessesByName(processName);
                if (processes.Length > 0)
                {
                    if (writeLog)
                        LogHelper.WriteLogToFile($"检测到WPS进程: {processName}", LogHelper.LogType.Info);
                    return true;
                }
            }
            return false;
        }

        private void TimerCheckPPT_Elapsed(object sender, ElapsedEventArgs e)
        {
            // 如果正在显示特定窗口，则跳过检查
            if (IsShowingRestoreHiddenSlidesWindow || IsShowingAutoplaySlidesWindow) 
                return;

            try
            {
                // 检查是否已有初始化的 PowerPoint 实例
                if (!isPowerPointInitialized)
                {
                    // 检测WPS和PowerPoint进程
                    bool wpsRunning = IsWPSRunning(true);
                    var pptProcesses = Process.GetProcessesByName("POWERPNT");
                    
                    // 根据设置和进程状态决定模式
                    isWPSMode = isWPSSupportOn && wpsRunning;
                    
                    LogHelper.WriteLogToFile($"初始化模式: {(isWPSMode ? "WPS" : "PowerPoint")}", LogHelper.LogType.Info);

                    // 优先获取WPS实例
                    if (isWPSMode)
                    {
                        try
                        {
                            // 尝试多种可能的ProgID
                            string[] possibleProgIds = { "wpp.Application", "WPS.Application" };
                            
                            foreach (var progId in possibleProgIds)
                            {
                                try
                                {
                                    LogHelper.WriteLogToFile($"尝试获取COM对象: {progId}", LogHelper.LogType.Info);
                                    pptApplication = (Microsoft.Office.Interop.PowerPoint.Application)Marshal.GetActiveObject(progId);
                                    if (pptApplication != null)
                                    {
                                        LogHelper.WriteLogToFile($"成功连接到WPS: {progId}", LogHelper.LogType.Info);
                                        break;
                                    }
                                }
                                catch 
                                {
                                    continue;
                                }
                            }
                        }
                        catch (COMException ex)
                        {
                            LogHelper.WriteLogToFile($"获取WPS实例失败: {ex.Message}", LogHelper.LogType.Error);
                            pptApplication = null;
                        }
                    }

                    // 如果未获取到WPS实例，尝试获取PowerPoint实例
                    if (pptApplication == null && pptProcesses.Length > 0)
                    {
                        try
                        {
                            LogHelper.WriteLogToFile("尝试获取PowerPoint实例", LogHelper.LogType.Info);
                            pptApplication = (Microsoft.Office.Interop.PowerPoint.Application)Marshal.GetActiveObject("PowerPoint.Application");
                            if (pptApplication != null)
                                LogHelper.WriteLogToFile("成功连接到PowerPoint", LogHelper.LogType.Info);
                        }
                        catch (COMException ex)
                        {
                            LogHelper.WriteLogToFile($"获取PowerPoint实例失败: {ex.Message}", LogHelper.LogType.Error);
                            pptApplication = null;
                        }
                    }

                    // 如果都没有找到，且未启用WPS支持，则自动创建PowerPoint进程
                    if (pptApplication == null && !isWPSMode && pptProcesses.Length == 0)
                    {
                        try
                        {
                            LogHelper.WriteLogToFile("尝试创建新的PowerPoint实例", LogHelper.LogType.Info);
                            pptApplication = (Microsoft.Office.Interop.PowerPoint.Application)Activator.CreateInstance(
                                Marshal.GetTypeFromCLSID(new Guid("91493441-5A91-11CF-8700-00AA0060263B")));
                            if (pptApplication != null)
                                LogHelper.WriteLogToFile("成功创建PowerPoint实例", LogHelper.LogType.Info);
                        }
                        catch (Exception ex)
                        {
                            LogHelper.WriteLogToFile($"创建PowerPoint实例失败: {ex.Message}", LogHelper.LogType.Error);
                            pptApplication = null;
                        }
                    }
                    
                    isPowerPointInitialized = true;
                    return;
                }

                // 检查进程是否还在
                bool currentWpsRunning = IsWPSRunning(false); // 定期检查不输出日志
                var currentPptProcesses = Process.GetProcessesByName("POWERPNT");
                
                // 检测应用程序是否关闭
                bool applicationClosed = isWPSMode ? !currentWpsRunning : currentPptProcesses.Length == 0;

                if (applicationClosed)
                {
                    LogHelper.WriteLogToFile($"{(isWPSMode ? "WPS" : "PowerPoint")}进程已关闭，清理对象", LogHelper.LogType.Info);
                    
                    // 进程已关闭，调用重置方法清理对象
                    ResetPresentationObjects();
                    isPowerPointInitialized = false;

                    // PowerPoint进程守护：自动重启PowerPoint进程（仅在未启用WPS支持时）
                    if (!isWPSSupportOn && !isWPSMode)
                    {
                        try
                        {
                            LogHelper.WriteLogToFile("尝试重启PowerPoint进程", LogHelper.LogType.Info);
                            pptApplication = (Microsoft.Office.Interop.PowerPoint.Application)Activator.CreateInstance(
                                Marshal.GetTypeFromCLSID(new Guid("91493441-5A91-11CF-8700-00AA0060263B")));
                            isPowerPointInitialized = true;
                            LogHelper.WriteLogToFile("PowerPoint进程重启成功", LogHelper.LogType.Info);
                        }
                        catch (Exception ex)
                        {
                            LogHelper.WriteLogToFile($"PowerPoint守护重启失败: {ex.Message}", LogHelper.LogType.Error);
                        }
                    }
                    return;
                }
                
                
                

                if (pptApplication != null) {
                    // 检查是否有活动演示文稿
                    if (pptApplication.Presentations.Count == 0 || pptApplication.ActivePresentation == null) 
                        return; // 退出方法，避免后续代码执行
                    isPowerPointInitialized = false;
                    timerCheckPPT.Stop();
                    //获得演示文稿对象
                    presentation = pptApplication.ActivePresentation;

                    // 获得幻灯片对象集合
                    slides = presentation.Slides;

                    // 获得幻灯片的数量
                    slidescount = slides.Count;
                    memoryStreams = new MemoryStream[slidescount + 2];
                    // 获得当前选中的幻灯片
                    try {
                        // 在普通视图下这种方式可以获得当前选中的幻灯片对象
                        // 然而在阅读模式下，这种方式会出现异常
                        slide = slides[pptApplication.ActiveWindow.Selection.SlideRange.SlideNumber];
                    }
                    catch {
                        // 在阅读模式下出现异常时，通过下面的方式来获得当前选中的幻灯片对象
                        try {
                            slide = pptApplication.SlideShowWindows[1].View.Slide;
                        }
                        catch (Exception ex) {
                            LogHelper.WriteLogToFile("Failed to get current slide: " + ex.ToString(), LogHelper.LogType.Error);
                        }
                    }

                    pptApplication.PresentationOpen += PptApplication_PresentationOpen;
                    pptApplication.PresentationClose += PptApplication_PresentationClose;
                    pptApplication.SlideShowBegin += PptApplication_SlideShowBegin;
                    pptApplication.SlideShowNextSlide += PptApplication_SlideShowNextSlide;
                    pptApplication.SlideShowEnd += PptApplication_SlideShowEnd;

                    // 检查是否在放映模式
                    Application.Current.Dispatcher.Invoke(() => {
                        try {
                            if (pptApplication.SlideShowWindows.Count >= 1) {
                                BtnPPTSlideShow.Visibility = Visibility.Collapsed;
                                BtnPPTSlideShowEnd.Visibility = Visibility.Visible;
                                UpdatePPTBtnDisplaySettingsStatus();
                            } else {
                                BtnPPTSlideShow.Visibility = Visibility.Visible;
                                BtnPPTSlideShowEnd.Visibility = Visibility.Collapsed;
                            }
                        } catch (Exception ex) {
                            LogHelper.WriteLogToFile("Failed to update button visibility: " + ex.ToString(), LogHelper.LogType.Error);
                        }
                    });
                }

                if (pptApplication == null) return;
                
                // 此处是已经开启了
                Application.Current.Dispatcher.Invoke(() => {
                    PptApplication_PresentationOpen(null);
                });
                

                //如果检测到已经开始放映，则立即进入画板模式
                if (pptApplication.SlideShowWindows.Count >= 1) {
                    Application.Current.Dispatcher.Invoke(() => {
                        PptApplication_SlideShowBegin(pptApplication.SlideShowWindows[1]);
                    });
                }
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile($"TimerCheckPPT_Elapsed 异常: {ex.Message}", LogHelper.LogType.Error);
                Application.Current.Dispatcher.Invoke(() => { 
                    BtnPPTSlideShow.Visibility = Visibility.Collapsed; 
                    timerCheckPPT.Start();
                });
            }
        }

        private void PptApplication_PresentationOpen(Presentation Pres) {
            // 跳转到上次播放页
            Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                try {
                    if (!Settings.PowerPointSettings.IsNotifyPreviousPage) return;
                    
                    var folderPath = Settings.Automation.AutoSavedStrokesLocation +
                                     @"\Auto Saved - Presentations\" + presentation.Name + "_" +
                                     presentation.Slides.Count;
                                     
                    if (!File.Exists(folderPath + "/Position")) return;
                    if (!int.TryParse(File.ReadAllText(folderPath + "/Position"), out var page)) return;
                    if (page <= 0) return;
                    
                    new YesOrNoNotificationWindow($"上次播放到了第 {page} 页, 是否立即跳转", () => {
                        if (pptApplication.SlideShowWindows.Count >= 1)
                            // 如果已经播放了的话, 跳转
                            presentation.SlideShowWindow.View.GotoSlide(page);
                        else
                            presentation.Windows[1].View.GotoSlide(page);
                    }).ShowDialog();
                }
                catch (Exception ex) {
                    LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                }
            }), DispatcherPriority.Normal);


            //检查是否有隐藏幻灯片
            if (Settings.PowerPointSettings.IsNotifyHiddenPage) {
                var isHaveHiddenSlide = false;
                foreach (Slide slide in slides)
                    if (slide.SlideShowTransition.Hidden == Microsoft.Office.Core.MsoTriState.msoTrue) {
                        isHaveHiddenSlide = true;
                        break;
                    }

                Application.Current.Dispatcher.BeginInvoke(new Action(() => {
                    if (isHaveHiddenSlide && !IsShowingRestoreHiddenSlidesWindow) {
                        IsShowingRestoreHiddenSlidesWindow = true;
                        new YesOrNoNotificationWindow("检测到此演示文档中包含隐藏的幻灯片，是否取消隐藏？",
                            () => {
                                foreach (Slide slide in slides)
                                    if (slide.SlideShowTransition.Hidden ==
                                        Microsoft.Office.Core.MsoTriState.msoTrue)
                                        slide.SlideShowTransition.Hidden =
                                            Microsoft.Office.Core.MsoTriState.msoFalse;
                                IsShowingRestoreHiddenSlidesWindow = false;
                            }, () => { IsShowingRestoreHiddenSlidesWindow = false; },
                            () => { IsShowingRestoreHiddenSlidesWindow = false; }).ShowDialog();
                    }

                    BtnPPTSlideShow.Visibility = Visibility.Visible;
                }), DispatcherPriority.Normal);
            }

            //检测是否有自动播放
            if (Settings.PowerPointSettings.IsNotifyAutoPlayPresentation
                && BtnPPTSlideShowEnd.Visibility != Visibility.Visible) {
                bool hasSlideTimings = false;
                foreach (Slide slide in presentation.Slides) {
                    if (slide.SlideShowTransition.AdvanceOnTime == MsoTriState.msoTrue &&
                        slide.SlideShowTransition.AdvanceTime > 0) {
                        hasSlideTimings = true;
                        break;
                    }
                }

                if (hasSlideTimings) {
                    Application.Current.Dispatcher.BeginInvoke((Action)(() => {
                        if (hasSlideTimings && !IsShowingAutoplaySlidesWindow) {
                            IsShowingAutoplaySlidesWindow = true;
                            new YesOrNoNotificationWindow("检测到此演示文档中自动播放或排练计时已经启用，可能导致幻灯片自动翻页，是否取消？",
                                () => {
                                    presentation.SlideShowSettings.AdvanceMode =
                                        PpSlideShowAdvanceMode.ppSlideShowManualAdvance;
                                    IsShowingAutoplaySlidesWindow = false;
                                }, () => { IsShowingAutoplaySlidesWindow = false; },
                                () => { IsShowingAutoplaySlidesWindow = false; }).ShowDialog();
                        }
                    }));
                    presentation.SlideShowSettings.AdvanceMode = PpSlideShowAdvanceMode.ppSlideShowManualAdvance;
                }
            }
        }

        private void PptApplication_PresentationClose(Presentation Pres) {
            try {
                pptApplication.PresentationOpen -= PptApplication_PresentationOpen;
                pptApplication.PresentationClose -= PptApplication_PresentationClose;
                pptApplication.SlideShowBegin -= PptApplication_SlideShowBegin;
                pptApplication.SlideShowNextSlide -= PptApplication_SlideShowNextSlide;
                pptApplication.SlideShowEnd -= PptApplication_SlideShowEnd;
                
                
                timerCheckPPT.Start();
                
                Application.Current.Dispatcher.Invoke(() => {
                    BtnPPTSlideShow.Visibility = Visibility.Collapsed;
                    BtnPPTSlideShowEnd.Visibility = Visibility.Collapsed;
                });
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }
        }

        private bool isPresentationHaveBlackSpace = false;
        private string pptName = null;

        private void UpdatePPTBtnStyleSettingsStatus() {
            try {
                var sopt = Settings.PowerPointSettings.PPTSButtonsOption.ToString();
                char[] soptc = sopt.ToCharArray();
                if (soptc[0] == '2')
                {
                    PPTLSPageButton.Visibility = Visibility.Visible;
                    PPTRSPageButton.Visibility = Visibility.Visible;
                }
                else
                {
                    PPTLSPageButton.Visibility = Visibility.Collapsed;
                    PPTRSPageButton.Visibility = Visibility.Collapsed;
                }
                if (soptc[2] == '2')
                {
                    // 这里先堆一点屎山，没空用Resources了
                    PPTBtnLSBorder.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTBtnRSBorder.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTBtnLSBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 91));
                    PPTBtnRSBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 91));
                    PPTLSPreviousButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTRSPreviousButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTLSNextButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTRSNextButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTLSPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTRSPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTLSPageButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTRSPageButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTLSNextButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTRSNextButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    TextBlock.SetForeground(PPTLSPageButton, new SolidColorBrush(Colors.White));
                    TextBlock.SetForeground(PPTRSPageButton, new SolidColorBrush(Colors.White));
                }
                else
                {
                    PPTBtnLSBorder.Background = new SolidColorBrush(Color.FromRgb(244, 244, 245));
                    PPTBtnRSBorder.Background = new SolidColorBrush(Color.FromRgb(244, 244, 245));
                    PPTBtnLSBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(161, 161, 170));
                    PPTBtnRSBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(161, 161, 170));
                    PPTLSPreviousButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTRSPreviousButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTLSNextButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTRSNextButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTLSPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTRSPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTLSPageButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTRSPageButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTLSNextButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTRSNextButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    TextBlock.SetForeground(PPTLSPageButton, new SolidColorBrush(Color.FromRgb(24, 24, 27)));
                    TextBlock.SetForeground(PPTRSPageButton, new SolidColorBrush(Color.FromRgb(24, 24, 27)));
                }
                if (soptc[1] == '2')
                {
                    PPTBtnLSBorder.Opacity = 0.5;
                    PPTBtnRSBorder.Opacity = 0.5;
                }
                else
                {
                    PPTBtnLSBorder.Opacity = 1;
                    PPTBtnRSBorder.Opacity = 1;
                }

                var bopt = Settings.PowerPointSettings.PPTBButtonsOption.ToString();
                char[] boptc = bopt.ToCharArray();
                if (boptc[0] == '2')
                {
                    PPTLBPageButton.Visibility = Visibility.Visible;
                    PPTRBPageButton.Visibility = Visibility.Visible;
                }
                else
                {
                    PPTLBPageButton.Visibility = Visibility.Collapsed;
                    PPTRBPageButton.Visibility = Visibility.Collapsed;
                }
                if (boptc[2] == '2')
                {
                    // 这里先堆一点屎山，没空用Resources了
                    PPTBtnLBBorder.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTBtnRBBorder.Background = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTBtnLBBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 91));
                    PPTBtnRBBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(82, 82, 91));
                    PPTLBPreviousButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTRBPreviousButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTLBNextButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTRBNextButtonGeometry.Brush = new SolidColorBrush(Colors.White);
                    PPTLBPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTRBPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTLBPageButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTRBPageButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTLBNextButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    PPTRBNextButtonFeedbackBorder.Background = new SolidColorBrush(Colors.White);
                    TextBlock.SetForeground(PPTLBPageButton, new SolidColorBrush(Colors.White));
                    TextBlock.SetForeground(PPTRBPageButton, new SolidColorBrush(Colors.White));
                }
                else
                {
                    PPTBtnLBBorder.Background = new SolidColorBrush(Color.FromRgb(244, 244, 245));
                    PPTBtnRBBorder.Background = new SolidColorBrush(Color.FromRgb(244, 244, 245));
                    PPTBtnLBBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(161, 161, 170));
                    PPTBtnRBBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(161, 161, 170));
                    PPTLBPreviousButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTRBPreviousButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTLBNextButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTRBNextButtonGeometry.Brush = new SolidColorBrush(Color.FromRgb(39, 39, 42));
                    PPTLBPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTRBPreviousButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTLBPageButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTRBPageButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTLBNextButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    PPTRBNextButtonFeedbackBorder.Background = new SolidColorBrush(Color.FromRgb(24, 24, 27));
                    TextBlock.SetForeground(PPTLBPageButton, new SolidColorBrush(Color.FromRgb(24, 24, 27)));
                    TextBlock.SetForeground(PPTRBPageButton, new SolidColorBrush(Color.FromRgb(24, 24, 27)));
                }
                if (boptc[1] == '2')
                {
                    PPTBtnLBBorder.Opacity = 0.5;
                    PPTBtnRBBorder.Opacity = 0.5;
                }
                else
                {
                    PPTBtnLBBorder.Opacity = 1;
                    PPTBtnRBBorder.Opacity = 1;
                }
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }
        }

        private void UpdatePPTBtnDisplaySettingsStatus() {
            try {
                // 检查是否应该显示PPT按钮
                bool shouldShowButtons = Settings.PowerPointSettings.ShowPPTButton && 
                    (BtnPPTSlideShowEnd.Visibility == Visibility.Visible || 
                    (pptApplication != null && pptApplication.SlideShowWindows.Count > 0));

                if (!shouldShowButtons)
                {
                    LeftBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                    RightBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                    LeftSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                    RightSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                    return;
                }

                var lsp = Settings.PowerPointSettings.PPTLSButtonPosition;
                LeftSidePanelForPPTNavigation.Margin = new Thickness(0, 0, 0, lsp*2);
                var rsp = Settings.PowerPointSettings.PPTRSButtonPosition;
                RightSidePanelForPPTNavigation.Margin = new Thickness(0, 0, 0, rsp*2);

                var dopt = Settings.PowerPointSettings.PPTButtonsDisplayOption.ToString();
                char[] doptc = dopt.ToCharArray();
                if (doptc[0] == '2') AnimationsHelper.ShowWithFadeIn(LeftBottomPanelForPPTNavigation);
                else LeftBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                if (doptc[1] == '2') AnimationsHelper.ShowWithFadeIn(RightBottomPanelForPPTNavigation);
                else RightBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                if (doptc[2] == '2') AnimationsHelper.ShowWithFadeIn(LeftSidePanelForPPTNavigation);
                else LeftSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                if (doptc[3] == '2') AnimationsHelper.ShowWithFadeIn(RightSidePanelForPPTNavigation);
                else RightSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }
        }

        private async void PptApplication_SlideShowBegin(SlideShowWindow Wn) {
            try {
                
                if (Settings.Automation.IsAutoFoldInPPTSlideShow && !isFloatingBarFolded)
                    await FoldFloatingBar(new object());
                else if (isFloatingBarFolded) await UnFoldFloatingBar(new object());

                isStopInkReplay = true;

                LogHelper.WriteLogToFile("PowerPoint Application Slide Show Begin", LogHelper.LogType.Event);

                await Application.Current.Dispatcher.InvokeAsync(() => {

                    //调整颜色
                    var screenRatio = SystemParameters.PrimaryScreenWidth / SystemParameters.PrimaryScreenHeight;
                    if (Math.Abs(screenRatio - 16.0 / 9) <= -0.01) {
                        if (Wn.Presentation.PageSetup.SlideWidth / Wn.Presentation.PageSetup.SlideHeight < 1.65) {
                            isPresentationHaveBlackSpace = true;

                            if (BtnSwitchTheme.Content.ToString() == "深色") {
                                //Light
                                BtnExit.Foreground = Brushes.White;
                                ThemeManager.Current.ApplicationTheme = ApplicationTheme.Dark;
                            } else {
                                //Dark
                            }
                        }
                    } else if (screenRatio == -256 / 135) { }

                    lastDesktopInkColor = 1;

                    slidescount = Wn.Presentation.Slides.Count;
                    previousSlideID = 0;
                    memoryStreams = new MemoryStream[slidescount + 2];

                    pptName = Wn.Presentation.Name;
                    LogHelper.NewLog("Name: " + Wn.Presentation.Name);
                    LogHelper.NewLog("Slides Count: " + slidescount.ToString());

                    //检查是否有已有墨迹，并加载
                    if (Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint)
                        if (Directory.Exists(Settings.Automation.AutoSavedStrokesLocation +
                                             @"\Auto Saved - Presentations\" + Wn.Presentation.Name + "_" +
                                             Wn.Presentation.Slides.Count)) {
                            LogHelper.WriteLogToFile("Found saved strokes", LogHelper.LogType.Trace);
                            var files = new DirectoryInfo(Settings.Automation.AutoSavedStrokesLocation +
                                                          @"\Auto Saved - Presentations\" + Wn.Presentation.Name + "_" +
                                                          Wn.Presentation.Slides.Count).GetFiles();
                            var count = 0;
                            foreach (var file in files)
                                if (file.Name != "Position") {
                                    var i = -1;
                                    try {
                                        i = int.Parse(Path.GetFileNameWithoutExtension(file.Name));
                                        memoryStreams[i] = new MemoryStream(File.ReadAllBytes(file.FullName));
                                        memoryStreams[i].Position = 0;
                                        count++;
                                    }
                                    catch (Exception ex) {
                                        LogHelper.WriteLogToFile(
                                            $"Failed to load strokes on Slide {i}\n{ex.ToString()}",
                                            LogHelper.LogType.Error);
                                    }
                                }

                            LogHelper.WriteLogToFile($"Loaded {count.ToString()} saved strokes");
                        }

                    StackPanelPPTControls.Visibility = Visibility.Visible;
                    UpdatePPTBtnDisplaySettingsStatus();
                    UpdatePPTBtnStyleSettingsStatus();

                    BtnPPTSlideShow.Visibility = Visibility.Collapsed;
                    BtnPPTSlideShowEnd.Visibility = Visibility.Visible;
                    ViewBoxStackPanelMain.Margin = new Thickness(10, 10, 10, 10);
                    ViewboxFloatingBar.Opacity = Settings.Appearance.ViewboxFloatingBarOpacityInPPTValue;

                    if (Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow &&
                        !Settings.Automation.IsAutoFoldInPPTSlideShow &&
                        GridTransparencyFakeBackground.Background == Brushes.Transparent && !isFloatingBarFolded) {
                        BtnHideInkCanvas_Click(BtnHideInkCanvas, null);
                    }

                    if (currentMode != 0)
                    {
                        ImageBlackboard_MouseUp(null,null);
                        BtnHideInkCanvas_Click(BtnHideInkCanvas, null);
                    }

                    BorderFloatingBarMainControls.Visibility = Visibility.Visible;

                    if (Settings.PowerPointSettings.IsShowCanvasAtNewSlideShow &&
                        !Settings.Automation.IsAutoFoldInPPTSlideShow)
                        BtnColorRed_Click(null, null);

                    isEnteredSlideShowEndEvent = false;
                    PPTBtnPageNow.Text = $"{Wn.View.CurrentShowPosition}";
                    PPTBtnPageTotal.Text = $"/ {Wn.Presentation.Slides.Count}";
                    LogHelper.NewLog("PowerPoint Slide Show Loading process complete");

                    if (!isFloatingBarFolded) {
                        new Thread(new ThreadStart(() => {
                            Thread.Sleep(100);
                            Application.Current.Dispatcher.Invoke(() => {
                                ViewboxFloatingBarMarginAnimation(60);
                            });
                        })).Start();
                    }
                });
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }
        }

        private bool isEnteredSlideShowEndEvent = false; //防止重复调用本函数导致墨迹保存失效

        private async void PptApplication_SlideShowEnd(Presentation Pres) {
            try {
                if (isFloatingBarFolded) await UnFoldFloatingBar(new object());

                LogHelper.WriteLogToFile(string.Format("PowerPoint Slide Show End"), LogHelper.LogType.Event);
                if (isEnteredSlideShowEndEvent) {
                    LogHelper.WriteLogToFile("Detected previous entrance, returning");
                    return;
                }

                isEnteredSlideShowEndEvent = true;
                if (Settings.PowerPointSettings.IsAutoSaveStrokesInPowerPoint) {
                   var folderPath = Settings.Automation.AutoSavedStrokesLocation + @"\Auto Saved - Presentations\" +
                                     Pres.Name + "_" + Pres.Slides.Count;
                    if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);
                    try {
                        File.WriteAllText(folderPath + "/Position", previousSlideID.ToString());
                    }
                    catch (Exception ex) {
                        LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                    }

                    for (var i = 1; i <= Pres.Slides.Count; i++)
                        if (memoryStreams[i] != null)
                            try {
                                if (memoryStreams[i].Length > 8) {
                                    var srcBuf = new byte[memoryStreams[i].Length];
                                    memoryStreams[i].Position = 0;
                                    var byteLength = memoryStreams[i].Read(srcBuf, 0, srcBuf.Length);
                                    // 使用Path.Combine构建文件路径
                                     File.WriteAllBytes(folderPath + @"\" + i.ToString("0000") + ".icstk", srcBuf);
                                    LogHelper.WriteLogToFile(string.Format(
                                        "Saved strokes for Slide {0}, size={1}, byteLength={2}", i.ToString(),
                                        memoryStreams[i].Length, byteLength));
                                } else {
                                     if (File.Exists(folderPath + @"\" + i.ToString("0000") + ".icstk"))
                                         File.Delete(folderPath + @"\" + i.ToString("0000") + ".icstk");
                                }
                            }
                            catch (Exception ex) {
                                LogHelper.WriteLogToFile(
                                    $"Failed to save strokes for Slide {i}\n{ex.ToString()}",
                                    LogHelper.LogType.Error);
                                if (File.Exists(folderPath + @"\" + i.ToString("0000") + ".icstk"))
                                    File.Delete(folderPath + @"\" + i.ToString("0000") + ".icstk");
                            }
                }

                await Application.Current.Dispatcher.InvokeAsync(() => {
                    try {
                        isPresentationHaveBlackSpace = false;

                        if (BtnSwitchTheme.Content.ToString() == "深色") {
                            //Light
                            BtnExit.Foreground = Brushes.Black;
                            ThemeManager.Current.ApplicationTheme = ApplicationTheme.Light;
                        } else {
                            //Dark
                        }

                        BtnPPTSlideShow.Visibility = Visibility.Visible;
                        BtnPPTSlideShowEnd.Visibility = Visibility.Collapsed;
                        StackPanelPPTControls.Visibility = Visibility.Collapsed;
                        LeftBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                        RightBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                        LeftSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                        RightSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;

                        ViewBoxStackPanelMain.Margin = new Thickness(10, 10, 10, 55);

                        if (currentMode != 0) {
                            CloseWhiteboardImmediately();
                            currentMode = 0;
                        }

                        ClearStrokes(true);

                        if (GridTransparencyFakeBackground.Background != Brushes.Transparent)
                            BtnHideInkCanvas_Click(BtnHideInkCanvas, null);

                        ViewboxFloatingBar.Opacity = Settings.Appearance.ViewboxFloatingBarOpacityValue;
                    }
                    catch (Exception ex) {
                        LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                    }
                });

                await Task.Delay(150);

                await Application.Current.Dispatcher.InvokeAsync(() => {
                    ViewboxFloatingBarMarginAnimation(100, true);
                });
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }
        }

        private int previousSlideID = 0;
        private MemoryStream[] memoryStreams = new MemoryStream[50];

        private void PptApplication_SlideShowNextSlide(SlideShowWindow Wn) {
            try {
                LogHelper.WriteLogToFile($"PowerPoint Next Slide (Slide {Wn.View.CurrentShowPosition})",
                    LogHelper.LogType.Event);
                if (Wn.View.CurrentShowPosition == previousSlideID) return;
                
                Application.Current.Dispatcher.Invoke(() => {
                    try {
                        var ms = new MemoryStream();
                        inkCanvas.Strokes.Save(ms);
                        ms.Position = 0;
                        memoryStreams[previousSlideID] = ms;

                        if (inkCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber &&
                            Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint && !_isPptClickingBtnTurned)
                            SaveScreenShot(true, Wn.Presentation.Name + "/" + Wn.View.CurrentShowPosition);
                        _isPptClickingBtnTurned = false;

                        ClearStrokes(true);
                        timeMachine.ClearStrokeHistory();

                        if (memoryStreams[Wn.View.CurrentShowPosition] != null &&
                            memoryStreams[Wn.View.CurrentShowPosition].Length > 0) {
                            memoryStreams[Wn.View.CurrentShowPosition].Position = 0;
                            inkCanvas.Strokes.Add(new StrokeCollection(memoryStreams[Wn.View.CurrentShowPosition]));
                        }

                        PPTBtnPageNow.Text = $"{Wn.View.CurrentShowPosition}";
                        PPTBtnPageTotal.Text = $"/ {Wn.Presentation.Slides.Count}";
                    }
                    catch (Exception ex) {
                        LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
                    }
                });
                
                previousSlideID = Wn.View.CurrentShowPosition;
            }
            catch (Exception ex) {
                LogHelper.WriteLogToFile(ex.ToString(), LogHelper.LogType.Error);
            }
        }

        private bool _isPptClickingBtnTurned = false;

       private void BtnPPTSlidesUp_Click(object sender, RoutedEventArgs e) {
            if (currentMode == 1) {
                GridBackgroundCover.Visibility = Visibility.Collapsed;
                AnimationsHelper.HideWithSlideAndFade(BlackboardLeftSide);
                AnimationsHelper.HideWithSlideAndFade(BlackboardCenterSide);
                AnimationsHelper.HideWithSlideAndFade(BlackboardRightSide);
                currentMode = 0;
            }

            _isPptClickingBtnTurned = true;

            if (inkCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber &&
                Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint)
                SaveScreenShot(true,
                    pptApplication.SlideShowWindows[1].Presentation.Name + "/" +
                    pptApplication.SlideShowWindows[1].View.CurrentShowPosition);

            try {
                new Thread(new ThreadStart(() => {
                    try {
                        pptApplication.SlideShowWindows[1].Activate();
                    }
                    catch {
                        // ignored
                    }

                    try {
                        pptApplication.SlideShowWindows[1].View.Previous();
                    }
                    catch {
                        // ignored
                    } // Without this catch{}, app will crash when click the pre-page button in the fir page in some special env.
                })).Start();
            }
            catch {
                StackPanelPPTControls.Visibility = Visibility.Collapsed;
                LeftBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                RightBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                LeftSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                RightSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnPPTSlidesDown_Click(object sender, RoutedEventArgs e) {
            if (currentMode == 1) {
                GridBackgroundCover.Visibility = Visibility.Collapsed;
                AnimationsHelper.HideWithSlideAndFade(BlackboardLeftSide);
                AnimationsHelper.HideWithSlideAndFade(BlackboardCenterSide);
                AnimationsHelper.HideWithSlideAndFade(BlackboardRightSide);
                currentMode = 0;
            }

            _isPptClickingBtnTurned = true;
            if (inkCanvas.Strokes.Count > Settings.Automation.MinimumAutomationStrokeNumber &&
                Settings.PowerPointSettings.IsAutoSaveScreenShotInPowerPoint)
                SaveScreenShot(true,
                    pptApplication.SlideShowWindows[1].Presentation.Name + "/" +
                    pptApplication.SlideShowWindows[1].View.CurrentShowPosition);
            try {
                new Thread(new ThreadStart(() => {
                    try {
                        pptApplication.SlideShowWindows[1].Activate();
                    }
                    catch {
                        // ignored
                    }

                    try {
                        pptApplication.SlideShowWindows[1].View.Next();
                    }
                    catch {
                        // ignored
                    }
                })).Start();
            }
            catch {
                StackPanelPPTControls.Visibility = Visibility.Collapsed;
                LeftBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                RightBottomPanelForPPTNavigation.Visibility = Visibility.Collapsed;
                LeftSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
                RightSidePanelForPPTNavigation.Visibility = Visibility.Collapsed;
            }
        }

        private async void PPTNavigationBtn_MouseDown(object sender, MouseButtonEventArgs e)
        {
            lastBorderMouseDownObject = sender;
            if (!Settings.PowerPointSettings.EnablePPTButtonPageClickable) return;
            if (sender == PPTLSPageButton)
            {
                PPTLSPageButtonFeedbackBorder.Opacity = 0.15;
            }
            else if (sender == PPTRSPageButton)
            {
                PPTRSPageButtonFeedbackBorder.Opacity = 0.15;
            }
            else if (sender == PPTLBPageButton)
            {
                PPTLBPageButtonFeedbackBorder.Opacity = 0.15;
            }
            else if (sender == PPTRBPageButton)
            {
                PPTRBPageButtonFeedbackBorder.Opacity = 0.15;
            }
        }

        private async void PPTNavigationBtn_MouseLeave(object sender, MouseEventArgs e)
        {
            lastBorderMouseDownObject = null;
            if (sender == PPTLSPageButton)
            {
                PPTLSPageButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRSPageButton)
            {
                PPTRSPageButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTLBPageButton)
            {
                PPTLBPageButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRBPageButton)
            {
                PPTRBPageButtonFeedbackBorder.Opacity = 0;
            }
        }

        private async void PPTNavigationBtn_MouseUp(object sender, MouseButtonEventArgs e) {
            if (lastBorderMouseDownObject != sender) return;

            if (sender == PPTLSPageButton)
            {
                PPTLSPageButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRSPageButton)
            {
                PPTRSPageButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTLBPageButton)
            {
                PPTLBPageButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRBPageButton)
            {
                PPTRBPageButtonFeedbackBorder.Opacity = 0;
            }

            if (!Settings.PowerPointSettings.EnablePPTButtonPageClickable) return;

            GridTransparencyFakeBackground.Opacity = 1;
            GridTransparencyFakeBackground.Background = new SolidColorBrush(StringToColor("#01FFFFFF"));
            CursorIcon_Click(null, null);
            try {
                pptApplication.SlideShowWindows[1].SlideNavigation.Visible = true;
            }
            catch { }

            // 控制居中
            if (!isFloatingBarFolded) {
                await Task.Delay(100);
                ViewboxFloatingBarMarginAnimation(60);
            }
        }

        private void BtnPPTSlideShow_Click(object sender, RoutedEventArgs e) {
            new Thread(new ThreadStart(() => {
                try {
                    presentation.SlideShowSettings.Run();
                }
                catch { }
            })).Start();
        }

        private async void BtnPPTSlideShowEnd_Click(object sender, RoutedEventArgs e) {
            Application.Current.Dispatcher.Invoke(() => {
                try {
                    var ms = new MemoryStream();
                    inkCanvas.Strokes.Save(ms);
                    ms.Position = 0;
                    memoryStreams[pptApplication.SlideShowWindows[1].View.CurrentShowPosition] = ms;
                    timeMachine.ClearStrokeHistory();
                }
                catch {
                    // ignored
                }
            });
            new Thread(new ThreadStart(() => {
                try {
                    pptApplication.SlideShowWindows[1].View.Exit();
                }
                catch {
                    // ignored
                }
            })).Start();

            HideSubPanels("cursor");
            await Task.Delay(150);
            ViewboxFloatingBarMarginAnimation(100, true);
        }

        private void GridPPTControlPrevious_MouseDown(object sender, MouseButtonEventArgs e)
        {
            lastBorderMouseDownObject = sender;
            if (sender == PPTLSPreviousButtonBorder) {
                PPTLSPreviousButtonFeedbackBorder.Opacity = 0.15;
            } else if (sender == PPTRSPreviousButtonBorder) {
                PPTRSPreviousButtonFeedbackBorder.Opacity = 0.15;
            } else if (sender == PPTLBPreviousButtonBorder)
            {
                PPTLBPreviousButtonFeedbackBorder.Opacity = 0.15;
            }
            else if (sender == PPTRBPreviousButtonBorder)
            {
                PPTRBPreviousButtonFeedbackBorder.Opacity = 0.15;
            }
        }
        private void GridPPTControlPrevious_MouseLeave(object sender, MouseEventArgs e)
        {
            lastBorderMouseDownObject = null;
            if (sender == PPTLSPreviousButtonBorder) {
                PPTLSPreviousButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTRSPreviousButtonBorder) {
                PPTRSPreviousButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTLBPreviousButtonBorder)
            {
                PPTLBPreviousButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRBPreviousButtonBorder)
            {
                PPTRBPreviousButtonFeedbackBorder.Opacity = 0;
            }
        }
        private void GridPPTControlPrevious_MouseUp(object sender, MouseButtonEventArgs e) {
            if (lastBorderMouseDownObject != sender) return;
            if (sender == PPTLSPreviousButtonBorder) {
                PPTLSPreviousButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTRSPreviousButtonBorder) {
                PPTRSPreviousButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTLBPreviousButtonBorder)
            {
                PPTLBPreviousButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRBPreviousButtonBorder)
            {
                PPTRBPreviousButtonFeedbackBorder.Opacity = 0;
            }
            BtnPPTSlidesUp_Click(BtnPPTSlidesUp, null);
        }


        private void GridPPTControlNext_MouseDown(object sender, MouseButtonEventArgs e) {
            lastBorderMouseDownObject = sender;
            if (sender == PPTLSNextButtonBorder) {
                PPTLSNextButtonFeedbackBorder.Opacity = 0.15;
            } else if (sender == PPTRSNextButtonBorder) {
                PPTRSNextButtonFeedbackBorder.Opacity = 0.15;
            } else if (sender == PPTLBNextButtonBorder)
            {
                PPTLBNextButtonFeedbackBorder.Opacity = 0.15;
            }
            else if (sender == PPTRBNextButtonBorder)
            {
                PPTRBNextButtonFeedbackBorder.Opacity = 0.15;
            }
        }
        private void GridPPTControlNext_MouseLeave(object sender, MouseEventArgs e)
        {
            lastBorderMouseDownObject = null;
            if (sender == PPTLSNextButtonBorder) {
                PPTLSNextButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTRSNextButtonBorder) {
                PPTRSNextButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTLBNextButtonBorder)
            {
                PPTLBNextButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRBNextButtonBorder)
            {
                PPTRBNextButtonFeedbackBorder.Opacity = 0;
            }
        }
        private void GridPPTControlNext_MouseUp(object sender, MouseButtonEventArgs e) {
            if (lastBorderMouseDownObject != sender) return;
            if (sender == PPTLSNextButtonBorder) {
                PPTLSNextButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTRSNextButtonBorder) {
                PPTRSNextButtonFeedbackBorder.Opacity = 0;
            } else if (sender == PPTLBNextButtonBorder)
            {
                PPTLBNextButtonFeedbackBorder.Opacity = 0;
            }
            else if (sender == PPTRBNextButtonBorder)
            {
                PPTRBNextButtonFeedbackBorder.Opacity = 0;
            }
            BtnPPTSlidesDown_Click(BtnPPTSlidesDown, null);
        }

        private void ImagePPTControlEnd_MouseUp(object sender, MouseButtonEventArgs e) {
            BtnPPTSlideShowEnd_Click(BtnPPTSlideShowEnd, null);
        }

        // 添加关机事件注册方法
        private void RegisterShutdownHandler()
        {
            try
            {
                SystemEvents.SessionEnding += SystemEvents_SessionEnding;
                LogHelper.WriteLogToFile("已注册系统关机事件处理", LogHelper.LogType.Info);
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"注册系统关机事件处理失败: {ex.Message}", LogHelper.LogType.Error);
            }
        }

        // 系统关机事件处理
        private void SystemEvents_SessionEnding(object sender, SessionEndingEventArgs e)
        {
            LogHelper.WriteLogToFile("检测到系统关机事件，正在清理PowerPoint进程", LogHelper.LogType.Info);
            
            // 终止PowerPoint进程守护
            try
            {
                // 停止计时器以终止进程守护
                timerCheckPPT.Stop();
                
                // 清理COM对象
                ResetPresentationObjects();
                
                // 强制结束所有PowerPoint进程
                foreach (var process in Process.GetProcessesByName("POWERPNT"))
                {
                    try
                    {
                        process.Kill();
                        LogHelper.WriteLogToFile($"已终止PowerPoint进程: {process.Id}", LogHelper.LogType.Info);
                    }
                    catch (Exception ex)
                    {
                        LogHelper.WriteLogToFile($"终止PowerPoint进程失败: {ex.Message}", LogHelper.LogType.Error);
                    }
                }
                
                // 强制结束所有WPS进程
                foreach (var processName in GetPossibleWPSProcessNames())
                {
                    foreach (var process in Process.GetProcessesByName(processName))
                    {
                        try
                        {
                            process.Kill();
                            LogHelper.WriteLogToFile($"已终止WPS进程: {process.ProcessName}({process.Id})", LogHelper.LogType.Info);
                        }
                        catch (Exception ex)
                        {
                            LogHelper.WriteLogToFile($"终止WPS进程失败: {ex.Message}", LogHelper.LogType.Error);
                        }
                    }
                }
                
                // 强制GC回收
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (Exception ex)
            {
                LogHelper.WriteLogToFile($"系统关机清理过程中出错: {ex.Message}", LogHelper.LogType.Error);
            }
        }
        
        // 在主窗口初始化方法中添加以下调用
        // 在适当的初始化方法中调用 RegisterShutdownHandler();
        
        // 在主窗口关闭时取消注册关机事件
        protected override void OnClosed(EventArgs e)
        {
            try
            {
                // 取消注册系统关机事件
                SystemEvents.SessionEnding -= SystemEvents_SessionEnding;
            }
            catch { }
            
            base.OnClosed(e);
        }
    }
}
