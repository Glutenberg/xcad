﻿//*********************************************************************
//xCAD
//Copyright(C) 2021 Xarial Pty Limited
//Product URL: https://www.xcad.net
//License: https://xcad.xarial.com/license/
//*********************************************************************

using SolidWorks.Interop.sldworks;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using Xarial.XCad.Annotations;
using Xarial.XCad.Documents;
using Xarial.XCad.Features.CustomFeature;
using Xarial.XCad.Features.CustomFeature.Attributes;
using Xarial.XCad.Features.CustomFeature.Enums;
using Xarial.XCad.Geometry;
using Xarial.XCad.Reflection;
using Xarial.XCad.SolidWorks.Documents;
using Xarial.XCad.SolidWorks.Features.CustomFeature.Exceptions;
using Xarial.XCad.SolidWorks.Features.CustomFeature.Toolkit;
using Xarial.XCad.SolidWorks.Geometry;
using Xarial.XCad.SolidWorks.Utils;
using Xarial.XCad.Toolkit.Exceptions;
using Xarial.XCad.Utils.CustomFeature;
using Xarial.XCad.Utils.Reflection;

namespace Xarial.XCad.SolidWorks.Features.CustomFeature
{
    public interface ISwMacroFeature : ISwFeature, IXCustomFeature
    {
    }

    internal class SwMacroFeature : SwFeature, ISwMacroFeature
    {
        private IMacroFeatureData m_FeatData;

        private Type m_DefinitionType;

        public Type DefinitionType 
        {
            get 
            {
                if (IsCommitted) 
                {
                    if (m_DefinitionType == null) 
                    {
                        var progId = FeatureData.GetProgId();

                        if (!string.IsNullOrEmpty(progId))
                        {
                            m_DefinitionType = Type.GetTypeFromProgID(progId);
                        }
                    }
                }

                return m_DefinitionType;
            }
            set 
            {
                if (!IsCommitted)
                {
                    m_DefinitionType = value;
                }
                else
                {
                    throw new CommittedElementPropertyChangeNotSupported();
                }
            }
        }

        public IMacroFeatureData FeatureData => m_FeatData ?? (m_FeatData = Feature.GetDefinition() as IMacroFeatureData);

        private readonly IFeatureManager m_FeatMgr;

        internal SwMacroFeature(IFeature feat, SwDocument doc, ISwApplication app, bool created)
            : base(feat, doc, app, created)
        {
            m_FeatMgr = doc.Model.FeatureManager;
        }

        //TODO: check constant context disconnection exception
        public IXConfiguration Configuration 
            => OwnerDocument.CreateObjectFromDispatch<SwConfiguration>(FeatureData.CurrentConfiguration);

        protected override IFeature CreateFeature(CancellationToken cancellationToken)
            => InsertComFeatureBase(null, null, null, null, null, null, null);

        protected IFeature InsertComFeatureBase(string[] paramNames, int[] paramTypes, string[] paramValues,
            int[] dimTypes, double[] dimValues, object[] selection, object[] editBodies)
        {
            ValidateDefinitionType();

            var options = CustomFeatureOptions_e.Default;
            var provider = "";

            DefinitionType.TryGetAttribute<CustomFeatureOptionsAttribute>(a =>
            {
                options = a.Flags;
            });

            DefinitionType.TryGetAttribute<MissingDefinitionErrorMessage>(a =>
            {
                provider = a.Message;
            });

            var baseName = MacroFeatureInfo.GetBaseName(DefinitionType);

            var progId = MacroFeatureInfo.GetProgId(DefinitionType);

            if (string.IsNullOrEmpty(progId))
            {
                throw new NullReferenceException("Prog id for macro feature cannot be extracted");
            }

            var icons = MacroFeatureIconInfo.GetIcons(DefinitionType,
                CompatibilityUtils.SupportsHighResIcons(SwMacroFeatureDefinition.Application.Sw, CompatibilityUtils.HighResIconsScope_e.MacroFeature));

            using (var selSet = new SelectionGroup(m_FeatMgr.Document.ISelectionManager))
            {
                if (selection != null && selection.Any())
                {
                    selSet.AddRange(selection);
                }

                var feat = m_FeatMgr.InsertMacroFeature3(baseName,
                    progId, null, paramNames, paramTypes,
                    paramValues, dimTypes, dimValues, editBodies, icons, (int)options) as IFeature;

                return feat;
            }
        }

        protected virtual void ValidateDefinitionType()
        {
            if (!typeof(SwMacroFeatureDefinition).IsAssignableFrom(DefinitionType))
            {
                throw new MacroFeatureDefinitionTypeMismatch(DefinitionType, typeof(SwMacroFeatureDefinition));
            }
        }
    }

    public interface ISwMacroFeature<TParams> : ISwMacroFeature, IXCustomFeature<TParams>
        where TParams : class, new()
    {
        /// <summary>
        /// Returns parameters without accessing the selection
        /// </summary>
        TParams CachedParameters { get; }
    }

    internal class SwMacroFeature<TParams> : SwMacroFeature, ISwMacroFeature<TParams>
        where TParams : class, new()
    {
        private readonly MacroFeatureParametersParser m_ParamsParser;
        private TParams m_ParametersCache;

        internal static SwMacroFeature CreateSpecificInstance(IFeature feat, SwDocument doc, ISwApplication app, Type paramType) 
        {
            var macroFeatType = typeof(SwMacroFeature<>).MakeGenericType(paramType);
            var paramsParser = new MacroFeatureParametersParser(app);

#if DEBUG
            //NOTE: this is a test to ensure that if constructor is changed the reflection will not be broken and this call will fail at compile time
            var test = new SwMacroFeature<object>(feat, doc, app, paramsParser, true);
#endif
            var constr = macroFeatType.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
                new Type[] { typeof(IFeature), typeof(SwDocument), typeof(ISwApplication), typeof(MacroFeatureParametersParser), typeof(bool) }, null);

            return (SwMacroFeature)constr.Invoke(new object[] { feat, doc, app, paramsParser, true });
        }

        //NOTE: this constructor is used in the reflection of SwObjectFactory
        internal SwMacroFeature(IFeature feat, SwDocument doc, ISwApplication app, MacroFeatureParametersParser paramsParser, bool created)
            : base(feat, doc, app, created)
        {
            m_ParamsParser = paramsParser;
        }

        public TParams Parameters
        {
            get
            {
                if (IsCommitted)
                {
                    if (FeatureData.AccessSelections(OwnerModelDoc, null))
                    {
                        return (TParams)m_ParamsParser.GetParameters(this, OwnerDocument, typeof(TParams),
                            out _, out _, out _, out _, out _);
                    }
                    else
                    {
                        throw new Exception("Failed to edit feature");
                    }
                }
                else
                {
                    return m_ParametersCache;
                }
            }
            set
            {
                if (IsCommitted)
                {
                    if (value == null)
                    {
                        FeatureData.ReleaseSelectionAccess();
                    }
                    else
                    {
                        m_ParamsParser.SetParameters(OwnerDocument, this, value, out _);

                        if (!Feature.ModifyDefinition(FeatureData, OwnerModelDoc, null))
                        {
                            throw new Exception("Failed to update parameters");
                        }
                    }
                }
                else
                {
                    m_ParametersCache = value;
                }
            }
        }

        public TParams CachedParameters =>
            (TParams)m_ParamsParser.GetParameters(this, OwnerDocument, typeof(TParams),
                out _, out _, out _, out _, out _);

        protected override IFeature CreateFeature(CancellationToken cancellationToken)
        {
            return InsertComFeatureWithParameters();
        }

        private IFeature InsertComFeatureWithParameters()
        {
            CustomFeatureParameter[] atts;
            IXSelObject[] selection;
            CustomFeatureDimensionType_e[] dimTypes;
            double[] dimValues;
            IXBody[] editBodies;

            m_ParamsParser.Parse(Parameters,
                out atts, out selection, out dimTypes, out dimValues,
                out editBodies);

            string[] paramNames;
            string[] paramValues;
            int[] paramTypes;

            m_ParamsParser.ConvertParameters(atts, out paramNames, out paramTypes, out paramValues);

            //TODO: add dim types conversion

            return InsertComFeatureBase(
                paramNames, paramTypes, paramValues,
                dimTypes?.Select(d => (int)d)?.ToArray(), dimValues,
                selection?.Cast<SwSelObject>()?.Select(s => s.Dispatch)?.ToArray(),
                editBodies?.Cast<SwBody>()?.Select(b => b.Body)?.ToArray());
        }

        protected override void ValidateDefinitionType()
        {
            if (!typeof(SwMacroFeatureDefinition<TParams>).IsAssignableFrom(DefinitionType))
            {
                throw new MacroFeatureDefinitionTypeMismatch(DefinitionType, typeof(SwMacroFeatureDefinition<TParams>));
            }
        }
    }
}