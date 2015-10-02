using MarkdownSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Microblog
{
	class Program
	{
		static void Main(string[] args)
		{
			var md = new Markdown(new MarkdownOptions()
			{

			});

			string html = md.Transform(File.ReadAllText("entries/0-initial.md"));
		}
	}
}
