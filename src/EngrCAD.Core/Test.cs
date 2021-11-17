using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core
{
    public class Test
    {

        public int Run()
        {
            return Wrapper.Test(@"C:\Users\chris\OneDrive\Desktop\n20.stp");
        }
    }
}
