using UnityEngine;

namespace Yozolab.YoluPainter.Editor
{
    internal static partial class PaintGui
    {
        internal const float ScrollbarWidth = 18;
        internal const float MinimumScrollThumb = 28;
        sealed class ScrollDrag
        {
            public ScrollDrag() { }
            internal float Offset;
        }
        internal static float ScrollContentWidth(Rect viewport, float contentHeight)
            => Mathf.Max(0, viewport.width - (contentHeight > viewport.height + .5f ? ScrollbarWidth : 0));
        internal static Rect ScrollTrack(Rect viewport)
            => new Rect(viewport.xMax - Mathf.Min(ScrollbarWidth, viewport.width), viewport.y, Mathf.Min(ScrollbarWidth, viewport.width), Mathf.Max(0, viewport.height));
        internal static Rect ScrollThumb(Rect viewport, float contentHeight, float position)
        {
            var track = ScrollTrack(viewport);
            float maximum = Mathf.Max(0, contentHeight - viewport.height);
            float height = contentHeight <= 0 ? track.height : Mathf.Min(track.height, Mathf.Max(MinimumScrollThumb, viewport.height * viewport.height / contentHeight));
            float y = maximum > 0 ? Mathf.Clamp(position, 0, maximum) / maximum * (track.height - height) : 0;
            return new Rect(track.x, track.y + y, track.width, height);
        }
        /// <summary>右端の18pxを掴める縦スクロール。つまみのドラッグ、溝のページ送り、ホイールを同じ範囲へ制限する。</summary>
        internal static bool Scrollbar(Rect viewport, ref Vector2 scroll, float contentHeight, float wheelStep = 14)
        {
            int id = GUIUtility.GetControlID("YoluPainterScroll".GetHashCode(), FocusType.Passive, viewport);
            var e = Event.current;
            float before = scroll.y; bool consumed = false;
            float maximum = Mathf.Max(0, contentHeight - Mathf.Max(0, viewport.height));
            scroll.x = 0; scroll.y = Mathf.Clamp(scroll.y, 0, maximum);
            if (maximum <= .5f || viewport.height <= 0 || viewport.width <= 0)
            {
                if (GUIUtility.hotControl == id) GUIUtility.hotControl = 0;
                scroll.y = 0;
                return before != scroll.y;
            }
            var track = ScrollTrack(viewport); var thumb = ScrollThumb(viewport, contentHeight, scroll.y);
            var drag = (ScrollDrag)GUIUtility.GetStateObject(typeof(ScrollDrag), id);
            switch (e.GetTypeForControl(id))
            {
                case EventType.MouseDown:
                    if (GUI.enabled && e.button == 0 && track.Contains(e.mousePosition))
                    {
                        if (thumb.Contains(e.mousePosition)) { drag.Offset = e.mousePosition.y - thumb.y; GUIUtility.hotControl = id; }
                        else scroll.y = Mathf.Clamp(scroll.y + (e.mousePosition.y < thumb.y ? -viewport.height : viewport.height), 0, maximum);
                        e.Use(); consumed = true;
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id)
                    {
                        float travel = track.height - thumb.height;
                        scroll.y = travel > 0 ? Mathf.Clamp01((e.mousePosition.y - track.y - drag.Offset) / travel) * maximum : 0;
                        e.Use(); consumed = true;
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id && e.button == 0) { GUIUtility.hotControl = 0; e.Use(); consumed = true; }
                    break;
                case EventType.KeyDown:
                    if (GUIUtility.hotControl == id && e.keyCode == KeyCode.Escape) { GUIUtility.hotControl = 0; e.Use(); consumed = true; }
                    break;
                case EventType.ScrollWheel:
                    if (GUIUtility.hotControl == 0 && viewport.Contains(e.mousePosition)) { scroll.y = Mathf.Clamp(scroll.y + e.delta.y * wheelStep, 0, maximum); e.Use(); consumed = true; }
                    break;
            }
            if (e.type == EventType.Repaint)
            {
                bool hover = track.Contains(e.mousePosition), held = GUIUtility.hotControl == id;
                Fill(track, hover || held ? PaintTheme.ControlBg : PaintTheme.PanelBg);
                thumb = ScrollThumb(viewport, contentHeight, scroll.y);
                float width = hover || held ? 12 : 6;
                var ink = new Rect(thumb.center.x - width / 2, thumb.y + 2, width, Mathf.Max(0, thumb.height - 4));
                Rounded(ink, held ? PaintTheme.Accent : hover ? PaintTheme.ControlHover : PaintTheme.ControlActive, width / 2);
            }
            return before != scroll.y || consumed;
        }
    }
}
