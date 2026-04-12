using System;

namespace Ink_Canvas.Helpers
{
    public class ComPPTLinkManager : BasePPTLinkManager
    {
        private readonly PPTManager _inner;

        public ComPPTLinkManager()
        {
            _inner = new PPTManager();

            _inner.SlideShowBegin += wn => OnSlideShowBegin(wn);
            _inner.SlideShowNextSlide += wn => OnSlideShowNextSlide(wn);
            _inner.SlideShowEnd += pres => OnSlideShowEnd(pres);
            _inner.PresentationOpen += pres => OnPresentationOpen(pres);
            _inner.PresentationClose += pres => OnPresentationClose(pres);
            _inner.PPTConnectionChanged += connected => OnPPTConnectionChanged(connected);
            _inner.SlideShowStateChanged += inSlideShow => OnSlideShowStateChanged(inSlideShow);
        }

        #region BasePPTLinkManager 属性重写
        public override bool IsConnected => _inner.IsConnected;

        public override bool IsInSlideShow => _inner.IsInSlideShow;

        public override bool IsSupportWPS
        {
            get => _inner.IsSupportWPS;
            set => _inner.IsSupportWPS = value;
        }

        public override bool SkipAnimationsWhenNavigating
        {
            get => _inner.SkipAnimationsWhenNavigating;
            set => _inner.SkipAnimationsWhenNavigating = value;
        }

        public override int SlidesCount => _inner.SlidesCount;

        public override object PPTApplication
        {
            get => _inner.PPTApplication;
            protected set { }
        }
        #endregion

        #region 生命周期管理
        public override void StartMonitoring() => _inner.StartMonitoring();

        public override void StopMonitoring() => _inner.StopMonitoring();

        public override void ReloadConnection()
        {
            LogHelper.WriteLogToFile("COM PPT 执行热重载：强制断开并重新连接", LogHelper.LogType.Event);
            _inner.StopMonitoring();
        }
        #endregion

        #region 放映控制
        public override bool TryStartSlideShow() => _inner.TryStartSlideShow();

        public override bool TryEndSlideShow() => _inner.TryEndSlideShow();
        #endregion

        #region 导航控制
        public override bool TryNavigateToSlide(int slideNumber) => _inner.TryNavigateToSlide(slideNumber);

        public override bool TryNavigateNext() => _inner.TryNavigateNext();

        public override bool TryNavigatePrevious() => _inner.TryNavigatePrevious();
        #endregion

        #region 查询
        public override int GetCurrentSlideNumber() => _inner.GetCurrentSlideNumber();

        public override string GetPresentationName() => _inner.GetPresentationName();

        public override bool TryShowSlideNavigation() => _inner.TryShowSlideNavigation();

        public override object GetCurrentActivePresentation() => _inner.GetCurrentActivePresentation();
        #endregion

        #region IDisposable
        public override void Dispose()
        {
            _inner?.Dispose();
        }
        #endregion
    }
}
