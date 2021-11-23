﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using EngrCADOCWrapper;

namespace EngrCAD.Core.Nodes
{
    public abstract class BaseNode : INode
    {
        /// <summary>
        /// List of child nodes
        /// </summary>
        public List<INode> Children { get; protected set; } = new();

        /// <summary>
        /// Default BaseNode Constructor
        /// </summary>
        protected BaseNode()
        {

        }

        /// <summary>
        /// BaseNode Constructor
        /// </summary>
        /// <param name="children">child nodes</param>
        protected BaseNode(IEnumerable<INode> children)
        {
            Children.AddRange(children);
        }


        public abstract NativeWrapper Generate();
    }
}
