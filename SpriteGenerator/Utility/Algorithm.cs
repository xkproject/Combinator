﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Piedone.Combinator.SpriteGenerator.Utility
{
    internal static class Algorithm
    {
        /// <summary>
        /// Greedy algorithm.
        /// </summary>
        /// <param name="modules">List of modules that represent the images that need to be inserted into the sprite.</param>
        /// <returns>Near optimal placement.</returns>
        public static Placement Greedy(IEnumerable<Module> modules)
        {
            var sortedByArea = from module in modules
                               orderby module.Width * module.Height descending
                               select module;

            //Empty O-Tree code.
            var oTree = new OTree();
            OT finalOT = null;
            //Empty list of modules.
            var moduleList = new List<Module>();

            //For each module which needs to be inserted.
            foreach (var module in sortedByArea)
            {
                OTree bestOTree = null;
                //Add module to the list of already packed modules.
                moduleList.Add(module);
                //Set the minimum perimeter of the placement to high.
                int minPerimeter = Int32.MaxValue;

                //Try all insertation point.
                foreach (int insertationPoint in oTree.InsertationPoints())
                {
                    var ot = oTree.Copy();
                    ot.Insert(module.Name, insertationPoint);
                    var oT = new OT(ot, moduleList);
                    var pm = oT.Placement;

                    //Choose the one with the minimum perimeter.
                    if (pm.Perimeter < minPerimeter)
                    {
                        finalOT = oT;
                        bestOTree = ot;
                        minPerimeter = pm.Perimeter;
                    }
                }
                oTree = bestOTree;
            }

            return finalOT.Placement;
        }
    }
}
