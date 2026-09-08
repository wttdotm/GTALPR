using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using LemonUI;
using LemonUI.Elements;

namespace FlockSurveillance
{
    internal sealed class LearnMorePopup :
        IProcessable,
        IRecalculable
    {
        private const float ScreenWidth = 1920f;
        private const float ScreenHeight = 1080f;
        private const float CardWidth = 1300f;
        private const float HeaderHeight = 120f;
        private const float HorizontalPadding = 150f;
        private const float ContentWidth =
            CardWidth - (HorizontalPadding * 2f);
        private const float ContentTopPadding = 42f;
        private const float QuestionLineSpacing = 3f;
        private const float QuestionToBodySpacing = 18f;
        private const float BodyLineSpacing = 3f;
        private const float FooterSpacing = 38f;
        private const float BottomPadding = 32f;
        private const float BodyTextScale = 0.51f;
        private const int MaximumLineUtf8Bytes = 88;

        private sealed class LearnMoreSection
        {
            public string Header { get; set; }

            public string Body { get; set; }
        }

        private readonly ScaledRectangle _screenShade =
            new ScaledRectangle(
                PointF.Empty,
                new SizeF(ScreenWidth, ScreenHeight)
            )
            {
                Color = Color.FromArgb(190, 0, 0, 0)
            };

        private readonly ScaledRectangle _card =
            new ScaledRectangle(
                PointF.Empty,
                new SizeF(CardWidth, 1f)
            )
            {
                Color = Color.FromArgb(240, 12, 12, 12)
            };

        private readonly ScaledRectangle _headerBar =
            new ScaledRectangle(
                PointF.Empty,
                new SizeF(CardWidth, HeaderHeight)
            )
            {
                Color = Color.Black
            };

        private readonly List<LearnMoreSection> _sections =
            new List<LearnMoreSection>();

        private readonly List<ScaledText> _questionLines =
            new List<ScaledText>();

        private readonly List<ScaledText> _bodyLines =
            new List<ScaledText>();

        private readonly ScaledText _title;
        private readonly ScaledText _questionMeasurement;
        private readonly ScaledText _footer;
        private readonly ScaledText _leftArrow;
        private readonly ScaledText _rightArrow;
        private readonly ScaledText _bodyMeasurement;

        private int _currentPage;
        private bool _escapeWasDown;
        private bool _layoutDirty = true;

        public LearnMorePopup()
        {
            _title = CreateText(
                PointF.Empty,
                "LEARN MORE",
                0.87f,
                GTA.UI.Font.Pricedown,
                GTA.UI.Alignment.Center
            );

            _questionMeasurement = CreateText(
                PointF.Empty,
                string.Empty,
                0.63f,
                GTA.UI.Font.ChaletLondon,
                GTA.UI.Alignment.Left
            );

            _footer = CreateText(
                PointF.Empty,
                string.Empty,
                0.42f,
                GTA.UI.Font.ChaletLondon,
                GTA.UI.Alignment.Center
            );

            _footer.Color = Color.LightGray;

            _leftArrow = CreateText(
                PointF.Empty,
                "~h~<~s~",
                0.90f,
                GTA.UI.Font.ChaletLondon,
                GTA.UI.Alignment.Center
            );

            _rightArrow = CreateText(
                PointF.Empty,
                "~h~>~s~",
                0.90f,
                GTA.UI.Font.ChaletLondon,
                GTA.UI.Alignment.Center
            );

            _bodyMeasurement = CreateText(
                PointF.Empty,
                string.Empty,
                BodyTextScale,
                GTA.UI.Font.ChaletLondon,
                GTA.UI.Alignment.Left
            );

            AddSection(
                "What is Flock?",
                "Flock is an $8.3 billion company that makes and sells " +
                "Automatic License Plate Readers (ALPRs or LPRs) to " +
                "local governments. ALPRs are AI-powered cameras that " +
                "capture and store information about all passing vehicles " +
                "without a warrant. " +
                "Your car's make, model, color, license " +
                "plate, location, heading, bumper stickers, dents, and more " +
                "are all stored and made searchable by the cops even if " +
                "you haven't done anything wrong. With over 100,000 cameras " +
                "currently deployed in the US according to ~g~DeFlock.org~s~, it is very likely that you, " +
                "yourself, your movements and life, are in their database."
            );

            AddSection(
                "Isn't it good to catch criminals though?",
                "These cameras don't monitor criminals. They monitor " +
                "everyone. There are many tools cops have to monitor " +
                "criminals that are more targeted and require a warrant or " +
                "have other oversight that are already extremely powerful. " +
                "These cameras don't have those limitations, they treat " +
                "everyone as a criminal waiting to be caught."
            );

            AddSection(
                "But I'm not a criminal?",
                "That doesn't mean you can't get caught! According to the " +
                "Institute for Justice, Flock only correctly reads 93% of " +
                "license plates, and inaccuracies like that pile up. In " +
                "Roseville, for example, Business Insider reported that of " +
                "the 1,427 alerts sent to cops reporting a vehicle as stolen " +
                "or used in a felony, 71% incorrectly read their suspected " +
                "plates. This error is serious, with the IJ and Electronic " +
                "Frontier Foundation reporting dozens of cases of dangerous " +
                "altercations between cops and civilians incited by incorrect " +
                "Flock reports, including many with guns drawn on " +
                "innocent people. You can experience this yourself as part " +
                "of the mod, which represents this systemic " +
                "inaccuracy with every camera sighting having a 5% chance to " +
                "call the cops called on you regardless of whether or not " +
                "you are wanted.\n\n" +
                "Sources:\n" +
                "~g~https://web.archive.org/web/20260805054344/~s~\n" +
                "~g~https://www.businessinsider.com/flock-camera-misread-license-~s~\n" +
                "~g~plate-reader-california-roseville-police-2026-7~s~\n" +
                "~g~https://ij.org/dozens-of-innocent-motorists-have-been-pulled-over-~s~\n" +
                "~g~detained-at-gunpoint-or-jailed-due-to-ai-license-plate-camera-errors/~s~\n" +
                "~g~https://www.eff.org/deeplinks/2024/11/human-toll-alpr-errors~s~"
            );

            AddSection(
                "That's fucked, what can I do to help?",
                "You can find Anti-Flock advocacy groups near you and learn " +
                "more at ~g~DeFlock.org~s~, as well as dive deeper into your " +
                "local " +
                "surveillance policies around Flock and other technologies " +
                "at the Electronic Frontier Foundation's project " +
                "~g~AtlasOfSurveillance.org~s~."
            );

            Recalculate();
        }

        public LearnMorePopup(
            string title,
            params KeyValuePair<string, string>[] sections
        )
            : this()
        {
            _title.Text = title;
            _sections.Clear();

            foreach (KeyValuePair<string, string> section in sections)
            {
                AddSection(section.Key, section.Value);
            }

            Recalculate();
        }

        public event EventHandler Closed;

        public bool Visible { get; set; }

        public void Open()
        {
            _currentPage = 0;
            _escapeWasDown =
                Game.IsKeyPressed(Keys.Escape);
            _layoutDirty = true;
            Visible = true;
        }

        public void Process()
        {
            if (!Visible)
            {
                return;
            }

            DisableControlsThisFrame();

            bool escapeDown =
                Game.IsKeyPressed(Keys.Escape);

            bool keyboardCloseRequested =
                escapeDown &&
                !_escapeWasDown;

            _escapeWasDown = escapeDown;

            bool controllerCloseRequested =
                IsDisabledFrontendControlJustPressed(
                    GTA.Control.FrontendCancel
                );

            if (
                keyboardCloseRequested ||
                controllerCloseRequested
            )
            {
                Visible = false;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (
                IsDisabledFrontendControlJustPressed(
                    GTA.Control.FrontendLeft
                )
            )
            {
                ChangePage(-1);
            }
            else if (
                IsDisabledFrontendControlJustPressed(
                    GTA.Control.FrontendRight
                )
            )
            {
                ChangePage(1);
            }

            if (_layoutDirty)
            {
                LayoutCurrentPage();
            }

            Draw();
        }

        public void Recalculate()
        {
            _screenShade.Recalculate();
            _card.Recalculate();
            _headerBar.Recalculate();
            _title.Recalculate();
            _questionMeasurement.Recalculate();
            _footer.Recalculate();
            _leftArrow.Recalculate();
            _rightArrow.Recalculate();
            _bodyMeasurement.Recalculate();

            foreach (ScaledText line in _questionLines)
            {
                line.Recalculate();
            }

            foreach (ScaledText line in _bodyLines)
            {
                line.Recalculate();
            }

            _layoutDirty = true;
        }

        private void AddSection(string header, string body)
        {
            _sections.Add(
                new LearnMoreSection
                {
                    Header = header,
                    Body = body
                }
            );
        }

        private void ChangePage(int change)
        {
            _currentPage =
                (
                    _currentPage +
                    change +
                    _sections.Count
                ) % _sections.Count;

            _layoutDirty = true;
        }

        private void LayoutCurrentPage()
        {
            LearnMoreSection section =
                _sections[_currentPage];

            _footer.Text = string.Format(
                "{0} / {1}    Left / Right or D-pad: navigate    " +
                "Esc or B: close",
                _currentPage + 1,
                _sections.Count
            );

            List<string> wrappedLines =
                new List<string>(
                    WrapParagraph(section.Body)
                );

            List<string> wrappedQuestionLines =
                new List<string>(
                    WrapText(
                        section.Header,
                        _questionMeasurement,
                        MaximumLineUtf8Bytes,
                        true
                    )
                );

            int maximumQuestionLineCount = 0;
            int maximumBodyLineCount = 0;

            foreach (LearnMoreSection candidateSection in _sections)
            {
                int questionLineCount = 0;

                foreach (
                    string ignored
                    in WrapText(
                        candidateSection.Header,
                        _questionMeasurement,
                        MaximumLineUtf8Bytes,
                        true
                    )
                )
                {
                    questionLineCount++;
                }

                maximumQuestionLineCount = Math.Max(
                    maximumQuestionLineCount,
                    questionLineCount
                );

                int lineCount = 0;

                foreach (
                    string ignored
                    in WrapParagraph(candidateSection.Body)
                )
                {
                    lineCount++;
                }

                maximumBodyLineCount = Math.Max(
                    maximumBodyLineCount,
                    lineCount
                );
            }

            float maximumQuestionHeight =
                maximumQuestionLineCount *
                _questionMeasurement.LineHeight;

            if (maximumQuestionLineCount > 1)
            {
                maximumQuestionHeight +=
                    (maximumQuestionLineCount - 1) *
                    QuestionLineSpacing;
            }

            float maximumBodyHeight =
                maximumBodyLineCount *
                _bodyMeasurement.LineHeight;

            if (maximumBodyLineCount > 1)
            {
                maximumBodyHeight +=
                    (maximumBodyLineCount - 1) *
                    BodyLineSpacing;
            }

            float cardHeight =
                HeaderHeight +
                ContentTopPadding +
                maximumQuestionHeight +
                QuestionToBodySpacing +
                maximumBodyHeight +
                FooterSpacing +
                _footer.LineHeight +
                BottomPadding;

            float cardX =
                (ScreenWidth - CardWidth) / 2f;
            float cardY =
                (ScreenHeight - cardHeight) / 2f;
            float contentX =
                cardX + HorizontalPadding;

            _card.Position =
                new PointF(cardX, cardY);
            _card.Size =
                new SizeF(CardWidth, cardHeight);

            _headerBar.Position =
                new PointF(cardX, cardY);
            _headerBar.Size =
                new SizeF(CardWidth, HeaderHeight);

            _title.Position =
                new PointF(
                    ScreenWidth / 2f,
                    cardY +
                    (
                        HeaderHeight -
                        _title.LineHeight
                    ) / 2f
                );

            float y =
                cardY +
                HeaderHeight +
                ContentTopPadding;

            float questionTop = y;

            _questionLines.Clear();

            foreach (string lineText in wrappedQuestionLines)
            {
                ScaledText line = CreateText(
                    new PointF(contentX, y),
                    "~h~" + lineText + "~s~",
                    0.63f,
                    GTA.UI.Font.ChaletLondon,
                    GTA.UI.Alignment.Left
                );

                _questionLines.Add(line);

                y +=
                    line.LineHeight +
                    QuestionLineSpacing;
            }

            y =
                questionTop +
                maximumQuestionHeight +
                QuestionToBodySpacing;

            _bodyLines.Clear();

            foreach (string lineText in wrappedLines)
            {
                ScaledText line = CreateText(
                    new PointF(contentX, y),
                    lineText,
                    BodyTextScale,
                    GTA.UI.Font.ChaletLondon,
                    GTA.UI.Alignment.Left
                );

                _bodyLines.Add(line);

                y +=
                    line.LineHeight +
                    BodyLineSpacing;
            }

            _footer.Position =
                new PointF(
                    ScreenWidth / 2f,
                    cardY +
                    cardHeight -
                    BottomPadding -
                    _footer.LineHeight
                );

            float arrowY =
                cardY +
                HeaderHeight +
                (
                    _footer.Position.Y -
                    cardY -
                    HeaderHeight -
                    _leftArrow.LineHeight
                ) / 2f;

            _leftArrow.Position =
                new PointF(
                    cardX + 65f,
                    arrowY
                );

            _rightArrow.Position =
                new PointF(
                    cardX +
                    CardWidth -
                    65f,
                    arrowY
                );

            _layoutDirty = false;
        }

        private IEnumerable<string> WrapParagraph(
            string paragraph
        )
        {
            return WrapText(
                paragraph,
                _bodyMeasurement,
                MaximumLineUtf8Bytes
            );
        }

        private IEnumerable<string> WrapText(
            string paragraph,
            ScaledText measurement,
            int maximumLineUtf8Bytes,
            bool bold = false
        )
        {
            const string explicitLineBreakToken = "\u0001";

            string normalizedParagraph = paragraph
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Replace(
                    "\n",
                    " " + explicitLineBreakToken + " "
                );

            string[] words = normalizedParagraph.Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries
            );

            StringBuilder currentLine =
                new StringBuilder();

            foreach (string word in words)
            {
                if (word == explicitLineBreakToken)
                {
                    if (currentLine.Length > 0)
                    {
                        yield return currentLine.ToString();
                        currentLine.Clear();
                    }
                    else
                    {
                        yield return " ";
                    }

                    continue;
                }

                string candidate =
                    currentLine.Length == 0
                        ? word
                        : currentLine + " " + word;

                if (
                    currentLine.Length > 0 &&
                    !FitsOnOneLine(
                        candidate,
                        measurement,
                        maximumLineUtf8Bytes,
                        bold
                    )
                )
                {
                    yield return currentLine.ToString();
                    currentLine.Clear();
                    currentLine.Append(word);
                    continue;
                }

                if (currentLine.Length > 0)
                {
                    currentLine.Append(' ');
                }

                currentLine.Append(word);
            }

            if (currentLine.Length > 0)
            {
                yield return currentLine.ToString();
            }
        }

        private bool FitsOnOneLine(
            string text,
            ScaledText measurement,
            int maximumLineUtf8Bytes,
            bool bold
        )
        {
            string measuredText =
                bold
                    ? "~h~" + text + "~s~"
                    : text;

            if (
                Encoding.UTF8.GetByteCount(measuredText) >
                maximumLineUtf8Bytes
            )
            {
                return false;
            }

            measurement.Text = measuredText;

            return
                measurement.Width <=
                ContentWidth;
        }

        private static ScaledText CreateText(
            PointF position,
            string text,
            float scale,
            GTA.UI.Font font,
            GTA.UI.Alignment alignment
        )
        {
            return new ScaledText(
                position,
                text,
                scale,
                font
            )
            {
                Alignment = alignment,
                Color = Color.White,
                WordWrap = 0f
            };
        }

        private void Draw()
        {
            _screenShade.Draw();
            _card.Draw();
            _headerBar.Draw();
            _title.Draw();

            foreach (ScaledText line in _questionLines)
            {
                line.Draw();
            }

            foreach (ScaledText line in _bodyLines)
            {
                line.Draw();
            }

            _leftArrow.Draw();
            _rightArrow.Draw();
            _footer.Draw();
        }

        private static bool IsDisabledFrontendControlJustPressed(
            GTA.Control control
        )
        {
            return Function.Call<bool>(
                Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                0,
                (int)control
            );
        }

        private static void DisableControlsThisFrame()
        {
            Game.DisableAllControlsThisFrame();

            Function.Call(
                Hash.DISABLE_ALL_CONTROL_ACTIONS,
                0
            );

            Function.Call(
                Hash.DISABLE_ALL_CONTROL_ACTIONS,
                1
            );

            Function.Call(
                Hash.DISABLE_ALL_CONTROL_ACTIONS,
                2
            );
        }
    }
}
