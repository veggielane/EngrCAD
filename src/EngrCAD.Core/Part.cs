using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EngrCAD.Core
{
    public class Part<T> where T : IPartMetadata
    {
        
    }

    public interface IPartMetadata
    {
        string Name { get; set; }
    }
}
