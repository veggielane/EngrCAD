using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using EngrCAD.Core.Datums;
using EngrCAD.Core.Sketcher;
using EngrCAD.Core.Sketcher.Edges;

namespace EngrCAD.Core
{
    public class SVG
    {
        public ICSYS CSYS { get; }
        public string Contents { get; }

        public IEnumerable<IClosedSketch> Sketches { get; }

        protected SVG(ICSYS csys, string contents)
        {
            CSYS = csys;
            Contents = contents;
            Sketches = ParseFile();
        }

        private IEnumerable<IClosedSketch> ParseFile()
        {
            var document = XDocument.Parse(Contents);
            var root = document.Root;
            var paths = root?.Descendants("path");

            return paths?.Select(e => ParsePath(e.Attribute("d")?.Value));
            
        }


        private (char, float[]) Tokenise(string segment)
        {
            var cmd = segment.Take(1).Single();
            var args = segment.Substring(1);
            var argSeparators = @"[\s,]|(?=-)";
            var splitArgs = Regex
                .Split(args, argSeparators)
                .Where(t => !string.IsNullOrEmpty(t));
            var floatArgs = splitArgs.Select(float.Parse).ToArray();
            return (cmd, floatArgs);
        }

        private IClosedSketch ParsePath(string path)
        { 
            var tokens = Regex.Split(path, @"(?=[MZLHVCSQTAmzlhvcsqta])").Where(t => !string.IsNullOrEmpty(t));
            var sketch = new Sketch(CSYS);
            foreach (var token in tokens)
            {
                var (command, args) = Tokenise(token);
                switch (command)
                {
                    case 'M':
                        sketch.MoveTo(args[0], -args[1]);
                        break;
                    case 'L':
                        sketch.LineTo(args[0], -args[1]);
                        break;
                    case 'Z':
                        return sketch.Close();
                    default:
                        throw new NotImplementedException(command.ToString());
                }
            }
            return null;//not closed
        }

        public static SVG FromFile(ICSYS csys, string filePath) => new SVG(csys, File.ReadAllText(filePath));
        public static SVG FromString(ICSYS csys, string contents) => new SVG(csys, contents);
    }
}