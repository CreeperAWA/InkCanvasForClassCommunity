using System;

namespace Ink_Canvas.Helpers
{
    public abstract class BasePPTLinkManager : IPPTLinkManager
    {
        #region IPPTLinkManager 事件
        public event Action<object> SlideShowBegin;
        public event Action<object> SlideShowNextSlide;
        public event Action<object> SlideShowEnd;
        public event Action<object> PresentationOpen;
        public event Action<object> PresentationClose;
        public event Action<bool> PPTConnectionChanged;
        public event Action<bool> SlideShowStateChanged;
        #endregion

        #region IPPTLinkManager 属性（默认实现）
        public virtual bool IsConnected => PPTApplication != null;

        public virtual bool IsInSlideShow { get; protected set; }

        public virtual bool IsSupportWPS { get; set; }

        public virtual bool SkipAnimationsWhenNavigating { get; set; }

        public virtual int SlidesCount { get; protected set; }

        public abstract object PPTApplication { get; protected set; }
        #endregion

        #region 构造函数
        protected BasePPTLinkManager()
        {
        }
        #endregion

        #region 生命周期管理（抽象方法，由子类实现）
        public abstract void StartMonitoring();

        public abstract void StopMonitoring();

        public virtual void ReloadConnection()
        {
            LogHelper.WriteLogToFile($"{GetType().Name} 执行热重载：强制断开并重新连接", LogHelper.LogType.Event);
            StopMonitoring();
        }
        #endregion

        #region 放映控制（抽象方法，由子类实现）
        public abstract bool TryStartSlideShow();

        public abstract bool TryEndSlideShow();
        #endregion

        #region 导航控制（抽象方法，由子类实现）
        public abstract bool TryNavigateToSlide(int slideNumber);

        public abstract bool TryNavigateNext();

        public abstract bool TryNavigatePrevious();
        #endregion

        #region 查询（抽象方法，由子类实现）
        public abstract int GetCurrentSlideNumber();

        public abstract string GetPresentationName();

        public abstract bool TryShowSlideNavigation();

        public abstract object GetCurrentActivePresentation();
        #endregion

        #region 事件触发辅助方法
        protected virtual void OnSlideShowBegin(object slideShowWindow)
        {
            SlideShowBegin?.Invoke(slideShowWindow);
        }

        protected virtual void OnSlideShowNextSlide(object slideShowWindow)
        {
            SlideShowNextSlide?.Invoke(slideShowWindow);
        }

        protected virtual void OnSlideShowEnd(object presentation)
        {
            SlideShowEnd?.Invoke(presentation);
        }

        protected virtual void OnPresentationOpen(object presentation)
        {
            PresentationOpen?.Invoke(presentation);
        }

        protected virtual void OnPresentationClose(object presentation)
        {
            PresentationClose?.Invoke(presentation);
        }

        protected virtual void OnPPTConnectionChanged(bool isConnected)
        {
            PPTConnectionChanged?.Invoke(isConnected);
        }

        protected virtual void OnSlideShowStateChanged(bool isInSlideShow)
        {
            SlideShowStateChanged?.Invoke(isInSlideShow);
        }
        #endregion

        #region IDisposable
        public virtual void Dispose()
        {
            StopMonitoring();
        }
        #endregion
    }
}
