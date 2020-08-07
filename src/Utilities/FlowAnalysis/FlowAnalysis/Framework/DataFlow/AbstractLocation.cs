﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Diagnostics;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis.FlowAnalysis.DataFlow.PointsToAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Microsoft.CodeAnalysis.FlowAnalysis.DataFlow
{
    /// <summary>
    /// <para>
    /// Represents an abstract analysis location.
    /// This is may be used to represent a location where an <see cref="DataFlow.AnalysisEntity"/> resides, i.e. <see cref="AnalysisEntity.InstanceLocation"/> or
    /// a location that is pointed to by a reference type variable, and tracked with <see cref="PointsToAnalysis.PointsToAnalysis"/>.
    /// </para>
    /// <para>
    /// An analysis location can be created for one of the following cases:
    ///     1. An allocation or an object creation operation (<see cref="CreateAllocationLocation(IOperation, ITypeSymbol, PointsToAnalysisContext)"/>).
    ///     2. Location for the implicit 'this' or 'Me' instance being analyzed (<see cref="CreateThisOrMeLocation(INamedTypeSymbol, ImmutableStack{IOperation})"/>).
    ///     3. Location created for certain symbols which do not have a declaration in executable code, i.e. no <see cref="IOperation"/> for declaration (such as parameter symbols, member symbols, etc. - <see cref="CreateSymbolLocation(ISymbol, ImmutableStack{IOperation})"/>/>).
    ///     4. Location created for flow capture entities, i.e. for <see cref="InterproceduralCaptureId"/> created for <see cref="IFlowCaptureOperation"/> or <see cref="IFlowCaptureReferenceOperation"/>.
    ///        See <see cref="CreateFlowCaptureLocation(InterproceduralCaptureId, ITypeSymbol, ImmutableStack{IOperation})"/>
    /// </para>
    /// </summary>
    public sealed class AbstractLocation : CacheBasedEquatable<AbstractLocation>
    {
        private readonly bool _isSpecialSingleton;
        public static readonly AbstractLocation Null = new AbstractLocation(creation: null, creationCallStack: null, analysisEntity: null, symbol: null, captureId: null, locationType: null, isSpecialSingleton: true);
        public static readonly AbstractLocation NoLocation = new AbstractLocation(creation: null, creationCallStack: null, analysisEntity: null, symbol: null, captureId: null, locationType: null, isSpecialSingleton: true);

        private AbstractLocation(IOperation? creation, ImmutableStack<IOperation>? creationCallStack, AnalysisEntity? analysisEntity, ISymbol? symbol, InterproceduralCaptureId? captureId, ITypeSymbol? locationType, bool isSpecialSingleton)
        {
            Debug.Assert(isSpecialSingleton ^ (locationType != null));

            Creation = creation;
            CreationCallStack = creationCallStack ?? ImmutableStack<IOperation>.Empty;
            AnalysisEntity = analysisEntity;
            Symbol = symbol;
            CaptureId = captureId;
            LocationType = locationType;
            _isSpecialSingleton = isSpecialSingleton;
        }

        private static AbstractLocation Create(IOperation? creation, ImmutableStack<IOperation>? creationCallStack, AnalysisEntity? analysisEntity, ISymbol? symbol, InterproceduralCaptureId? captureId, ITypeSymbol? locationType)
        {
            Debug.Assert(creation != null ^ symbol != null ^ analysisEntity != null ^ captureId != null);
            Debug.Assert(locationType != null);

            return new AbstractLocation(creation, creationCallStack, analysisEntity, symbol, captureId, locationType, isSpecialSingleton: false);
        }

        public static AbstractLocation CreateAllocationLocation(IOperation creation, ITypeSymbol locationType, PointsToAnalysisContext analysisContext)
            => CreateAllocationLocation(creation, locationType, analysisContext.InterproceduralAnalysisData?.CallStack);
        internal static AbstractLocation CreateAllocationLocation(IOperation creation, ITypeSymbol locationType, ImmutableStack<IOperation>? callStack)
            => Create(creation, callStack, analysisEntity: null, symbol: null, captureId: null, locationType: locationType);
        public static AbstractLocation CreateAnalysisEntityDefaultLocation(AnalysisEntity analysisEntity)
            => Create(creation: null, creationCallStack: null, analysisEntity: analysisEntity, symbol: null, captureId: null, locationType: analysisEntity.Type);
        public static AbstractLocation CreateThisOrMeLocation(INamedTypeSymbol namedTypeSymbol, ImmutableStack<IOperation>? creationCallStack)
            => Create(creation: null, creationCallStack: creationCallStack, analysisEntity: null, symbol: namedTypeSymbol, captureId: null, locationType: namedTypeSymbol);
        public static AbstractLocation CreateSymbolLocation(ISymbol symbol, ImmutableStack<IOperation>? creationCallStack)
            => Create(creation: null, creationCallStack: creationCallStack, analysisEntity: null, symbol: symbol, captureId: null, locationType: symbol.GetMemberOrLocalOrParameterType());
        public static AbstractLocation CreateFlowCaptureLocation(InterproceduralCaptureId captureId, ITypeSymbol locationType, ImmutableStack<IOperation>? creationCallStack)
            => Create(creation: null, creationCallStack: creationCallStack, analysisEntity: null, symbol: null, captureId: captureId, locationType: locationType);

        public IOperation? Creation { get; }
        public ImmutableStack<IOperation> CreationCallStack { get; }

        /// <summary>
        /// Returns the top of <see cref="CreationCallStack"/> if this location was created through an interprocedural method invocation, i.e. <see cref="CreationCallStack"/> is non-empty.
        /// Otherwise, returns <see cref="Creation"/>.
        /// </summary>
        public IOperation? GetTopOfCreationCallStackOrCreation()
        {
            if (CreationCallStack.IsEmpty)
            {
                return Creation;
            }

            return CreationCallStack.Peek();
        }

        public AnalysisEntity? AnalysisEntity { get; }
        public ISymbol? Symbol { get; }
        public InterproceduralCaptureId? CaptureId { get; }
        public ITypeSymbol? LocationType { get; }
        public bool IsNull => ReferenceEquals(this, Null);
        public bool IsNoLocation => ReferenceEquals(this, NoLocation);

        /// <summary>
        /// Indicates this represents the initial unknown but distinct location for an analysis entity.
        /// </summary>
        public bool IsAnalysisEntityDefaultLocation => AnalysisEntity != null;

        protected override void ComputeHashCodeParts(Action<int> addPart)
        {
            addPart(Creation.GetHashCodeOrDefault());
            addPart(HashUtilities.Combine(CreationCallStack));
            addPart(Symbol.GetHashCodeOrDefault());
            addPart(CaptureId.GetHashCodeOrDefault());
            addPart(AnalysisEntity.GetHashCodeOrDefault());
            addPart(LocationType.GetHashCodeOrDefault());
            addPart(_isSpecialSingleton.GetHashCode());
            addPart(IsNull.GetHashCode());
        }

        /// <summary>
        /// Attempts to get the syntax node to report diagnostic for this abstract location 
        /// Returns null if the location is owned by another method invoked through interprocedural analysis.
        /// </summary>
        public SyntaxNode? TryGetNodeToReportDiagnostic(PointsToAnalysisResult? pointsToAnalysisResult)
        {
            Debug.Assert(Creation != null);

            if (pointsToAnalysisResult != null)
            {
                // Attempt to report diagnostic at the bottommost stack frame that owns the location.
                foreach (var creation in CreationCallStack)
                {
                    var syntaxNode = TryGetSyntaxNodeToReportDiagnostic(creation, pointsToAnalysisResult);
                    if (syntaxNode != null)
                    {
                        return syntaxNode;
                    }

                    if (!(creation is IInvocationOperation invocation) ||
                        !invocation.TargetMethod.IsLambdaOrLocalFunctionOrDelegate())
                    {
                        return null;
                    }
                }
            }

            // Fallback to reporting the diagnostic on the allocation location.
            return Creation?.Syntax;

            // Local functions.
            SyntaxNode? TryGetSyntaxNodeToReportDiagnostic(IOperation creation, PointsToAnalysisResult pointsToAnalysisResult)
            {
                // If any of the argument to creation points to this location, then use the argument.
                var arguments = creation switch
                {
                    IInvocationOperation invocation => invocation.Arguments,

                    IObjectCreationOperation objectCreation => objectCreation.Arguments,

                    _ => ImmutableArray<IArgumentOperation>.Empty,
                };

                foreach (var argument in arguments)
                {
                    var syntaxNode = TryGetSyntaxNodeToReportDiagnosticCore(argument);
                    if (syntaxNode != null)
                    {
                        return syntaxNode;
                    }
                }

                return TryGetSyntaxNodeToReportDiagnosticCore(creation);

                SyntaxNode? TryGetSyntaxNodeToReportDiagnosticCore(IOperation operation)
                {
                    var pointsToValue = pointsToAnalysisResult[operation];
                    return TryGetSyntaxNodeToReportDiagnosticForPointsValue(pointsToValue, operation);

                    SyntaxNode? TryGetSyntaxNodeToReportDiagnosticForPointsValue(PointsToAbstractValue pointsToValue, IOperation operation)
                    {
                        foreach (var location in pointsToValue.Locations)
                        {
                            if (location == this)
                            {
                                return operation.Syntax;
                            }
                        }

                        if (pointsToAnalysisResult.TaskWrappedValuesMap != null &&
                            pointsToAnalysisResult.TaskWrappedValuesMap.TryGetValue(pointsToValue, out var wrappedValue))
                        {
                            return TryGetSyntaxNodeToReportDiagnosticForPointsValue(wrappedValue, operation);
                        }

                        return null;
                    }
                }
            }
        }
    }
}
