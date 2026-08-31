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
        private const float ContentX = 435f;
        private const float ContentWidth = 1050f;
        private const float FirstSectionY = 145f;
        private const float HeaderToBodySpacing = 7f;
        private const float SectionSpacing = 18f;
        private const float BodyLineSpacing = 1f;
        private const float BodyTextScale = 0.285f;
        private const int MaximumLineUtf8Bytes = 88;

        private sealed class LearnMoreSection
        {
            public ScaledText Header { get; set; }

            public string Body { get; set; }

            public List<ScaledText> BodyLines { get; } =
                new List<ScaledText>();
        }

        private readonly ScaledRectangle _screenShade =
            new ScaledRectangle(
                PointF.Empty,
                new SizeF(1920f, 1080f)
            )
            {
                Color = Color.FromArgb(205, 0, 0, 0)
            };

        private readonly ScaledRectangle _panel =
            new ScaledRectangle(
                new PointF(110f, 35f),
                new SizeF(1700f, 1010f)
            )
            {
                Color = Color.FromArgb(245, 12, 12, 12)
            };

        private readonly ScaledRectangle _headerBar =
            new ScaledRectangle(
                new PointF(110f, 35f),
                new SizeF(1700f, 90f)
            )
            {
                Color = Color.Black
            };

        private readonly List<LearnMoreSection> _sections =
            new List<LearnMoreSection>();

        private readonly ScaledText _title;
        private readonly ScaledText _closeHint;
        private readonly ScaledText _bodyMeasurement;

        private bool _escapeWasDown;
        private bool _layoutDirty = true;

        public LearnMorePopup()
        {
            _title = CreateText(
                new PointF(960f, 52f),
                "LEARN MORE",
                0.62f,
                GTA.UI.Font.Pricedown,
                0f,
                GTA.UI.Alignment.Center
            );

            _bodyMeasurement = CreateText(
                PointF.Empty,
                string.Empty,
                BodyTextScale,
                GTA.UI.Font.ChaletLondon,
                0f,
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
                "currently deployed in the US, it is very likely that you, " +
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
                "That doesn't mean you can't get caught! Flock has a 5% " +
                "misreport rate, and across hundreds of thousands of " +
                "cameras, that means there are countless incidents of cops " +
                "chasing, detaining, and falsely accusing innocent civilians " +
                "of crimes that they never actually committed. You can " +
                "experience this yourself as part of the mod, as every time " +
                "you pass by a camera without a wanted level, there is a 5% " +
                "chance you get the cops called on you regardless."
            );

            AddSection(
                "That's fucked, what can I do to help?",
                "You can find Anti-Flock advocacy groups near you and learn " +
                "more at DeFlock.org, as well as dive deeper into your local " +
                "surveillance " +
                "policies around Flock and other technologies at the " +
                "Electronic Frontier Foundation's project " +
                "AtlasOfSurveillance.org."
            );

            _closeHint = CreateText(
                new PointF(960f, 985f),
                "Esc or B on controller to close",
                0.32f,
                GTA.UI.Font.ChaletLondon,
                0f,
                GTA.UI.Alignment.Center
            );

            _closeHint.Color = Color.LightGray;

            Recalculate();
        }

        public event EventHandler Closed;

        public bool Visible { get; set; }

        public void Open()
        {
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
                Function.Call<bool>(
                    Hash.IS_DISABLED_CONTROL_JUST_PRESSED,
                    0,
                    (int)GTA.Control.FrontendCancel
                );

            if (_layoutDirty)
            {
                LayoutSections();
            }

            Draw();

            if (
                keyboardCloseRequested ||
                controllerCloseRequested
            )
            {
                Visible = false;
                Closed?.Invoke(this, EventArgs.Empty);
            }
        }

        public void Recalculate()
        {
            _screenShade.Recalculate();
            _panel.Recalculate();
            _headerBar.Recalculate();
            _title.Recalculate();
            _closeHint.Recalculate();
            _bodyMeasurement.Recalculate();

            foreach (LearnMoreSection section in _sections)
            {
                section.Header.Recalculate();

                foreach (ScaledText line in section.BodyLines)
                {
                    line.Recalculate();
                }
            }

            _layoutDirty = true;
        }

        private void AddSection(string header, string body)
        {
            _sections.Add(
                new LearnMoreSection
                {
                    Header = CreateText(
                        PointF.Empty,
                        "~h~" + header + "~s~",
                        0.37f,
                        GTA.UI.Font.ChaletLondon,
                        0f,
                        GTA.UI.Alignment.Left
                    ),
                    Body = body
                }
            );
        }

        private void LayoutSections()
        {
            float y = FirstSectionY;

            foreach (LearnMoreSection section in _sections)
            {
                section.BodyLines.Clear();

                section.Header.Position =
                    new PointF(ContentX, y);

                y +=
                    section.Header.LineHeight +
                    HeaderToBodySpacing;

                foreach (
                    string lineText
                    in WrapParagraph(section.Body)
                )
                {
                    ScaledText line = CreateText(
                        new PointF(ContentX, y),
                        lineText,
                        BodyTextScale,
                        GTA.UI.Font.ChaletLondon,
                        0f,
                        GTA.UI.Alignment.Left
                    );

                    section.BodyLines.Add(line);

                    y +=
                        line.LineHeight +
                        BodyLineSpacing;
                }

                y += SectionSpacing;
            }

            _layoutDirty = false;
        }

        private IEnumerable<string> WrapParagraph(
            string paragraph
        )
        {
            string[] words = paragraph.Split(
                new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries
            );

            StringBuilder currentLine =
                new StringBuilder();

            foreach (string word in words)
            {
                string candidate =
                    currentLine.Length == 0
                        ? word
                        : currentLine + " " + word;

                if (
                    currentLine.Length > 0 &&
                    !FitsOnOneLine(candidate)
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

        private bool FitsOnOneLine(string text)
        {
            if (
                Encoding.UTF8.GetByteCount(text) >
                MaximumLineUtf8Bytes
            )
            {
                return false;
            }

            _bodyMeasurement.Text = text;

            return
                _bodyMeasurement.Width <=
                ContentWidth;
        }

        private static ScaledText CreateText(
            PointF position,
            string text,
            float scale,
            GTA.UI.Font font,
            float wordWrap,
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
                WordWrap = wordWrap
            };
        }

        private void Draw()
        {
            _screenShade.Draw();
            _panel.Draw();
            _headerBar.Draw();
            _title.Draw();

            foreach (LearnMoreSection section in _sections)
            {
                section.Header.Draw();

                foreach (ScaledText line in section.BodyLines)
                {
                    line.Draw();
                }
            }

            _closeHint.Draw();
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
