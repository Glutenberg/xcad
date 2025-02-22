﻿//*********************************************************************
//xCAD
//Copyright(C) 2021 Xarial Pty Limited
//Product URL: https://www.xcad.net
//License: https://xcad.xarial.com/license/
//*********************************************************************

using System;
using System.Collections.Generic;
using System.Text;
using Xarial.XCad.Geometry.Structures;
using Xarial.XCad.Geometry.Wires;

namespace Xarial.XCad.Geometry
{
    /// <summary>
    /// Represents the closed planar region
    /// </summary>
    public interface IXRegion
    {
        /// <summary>
        /// Plane defining this region
        /// </summary>
        Plane Plane { get; }

        /// <summary>
        /// Boundary of this region
        /// </summary>
        IXSegment[] Boundary { get; }
    }
}
