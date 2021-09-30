﻿using System;
using System.Collections.Generic;
using System.Text;
using Xarial.XCad.Exceptions;

namespace Xarial.XCad.SolidWorks.Geometry.Exceptions
{
    /// <summary>
    /// IMassProperty API in SOLIDOWRKS 2019 failed to correctly calculate the Principal Axes Of Inertia for the components
    /// </summary>
    public class PrincipalAxesOfInertiaOverridenException : NotSupportedException, IUserException
    {
        internal PrincipalAxesOfInertiaOverridenException(string reason)
            : base($"Failed to calculate Principal Axes Of Intertia for in SOLIDWORKS 2019 for the overriden mass properties: {reason}") 
        {
        }
    }
}
