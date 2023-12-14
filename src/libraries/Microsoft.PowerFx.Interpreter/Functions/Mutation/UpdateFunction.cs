﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PowerFx.Core.App.ErrorContainers;
using Microsoft.PowerFx.Core.Binding;
using Microsoft.PowerFx.Core.Errors;
using Microsoft.PowerFx.Core.Functions;
using Microsoft.PowerFx.Core.Localization;
using Microsoft.PowerFx.Core.Texl.Builtins;
using Microsoft.PowerFx.Core.Types;
using Microsoft.PowerFx.Core.Utils;
using Microsoft.PowerFx.Functions;
using Microsoft.PowerFx.Syntax;
using Microsoft.PowerFx.Types;
using static Microsoft.PowerFx.Core.Localization.TexlStrings;

namespace Microsoft.PowerFx.Interpreter.Functions.Mutation
{
    internal class UpdateFunction : PatchAndValidateRecordFunctionBase, IAsyncTexlFunction
    {
        public UpdateFunction()
            : base("Update", AboutUpdate, FunctionCategories.Table | FunctionCategories.Behavior, DType.EmptyRecord, 0, 3, 4, DType.EmptyTable, DType.EmptyRecord, DType.EmptyRecord, DType.EmptyEnum)
        {
        }

        public override bool MutatesArg0 => true;

        public override bool IsSelfContained => false;

        protected static new async Task<Dictionary<string, FormulaValue>> CreateRecordFromArgsDictAsync(FormulaValue[] args, int startFrom, CancellationToken cancellationToken)
        {
            var retFields = new Dictionary<string, FormulaValue>(StringComparer.Ordinal);

            for (var i = startFrom; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg is RecordValue record)
                {
                    await foreach (var field in record.GetFieldsAsync(cancellationToken).ConfigureAwait(false))
                    {
                        retFields[field.Name] = field.Value;
                    }
                }
                else
                {
                    throw new ArgumentException($"Can't handle {arg.Type} argument type.");
                }
            }

            return retFields;
        }

        public override bool TryGetTypeForArgSuggestionAt(int argIndex, out DType type)
        {
            if (argIndex == 1 || argIndex == 2)
            {
                type = default;
                return false;
            }

            return base.TryGetTypeForArgSuggestionAt(argIndex, out type);
        }

        public override bool IsLazyEvalParam(int index, Features features)
        {
            // First argument to mutation functions is Lazy for datasources that are copy-on-write.
            // If there are any side effects in the arguments, we want those to have taken place before we make the copy.
            return index == 0;
        }

        public override IEnumerable<StringGetter[]> GetSignatures()
        {
            yield return new[] { UpdateDataSourceArg, UpdateBaseOldRecordArg, UpdateChangeRecordArg };
            yield return new[] { UpdateDataSourceArg, UpdateBaseOldRecordArg, UpdateChangeRecordArg, UpdateRemoveFlagsArg };
        }

        public override IEnumerable<StringGetter[]> GetSignatures(int arity)
        {
            if (arity > 3)
            {
                return GetGenericSignatures(arity, UpdateDataSourceArg, UpdateBaseOldRecordArg, UpdateChangeRecordArg, UpdateRemoveFlagsArg);
            }

            return base.GetSignatures(arity);
        }

        public override bool CheckTypes(CheckTypesContext context, TexlNode[] args, DType[] argTypes, IErrorContainer errors, out DType returnType, out Dictionary<TexlNode, DType> nodeToCoercedTypeMap)
        {
            Contracts.AssertValue(args);
            Contracts.AssertAllValues(args);
            Contracts.AssertValue(argTypes);
            Contracts.Assert(args.Length == argTypes.Length);
            Contracts.AssertValue(errors);

            var isValid = base.CheckTypes(context, args, argTypes, errors, out returnType, out nodeToCoercedTypeMap);

            DType dataSourceType = argTypes[0];

            if (!dataSourceType.IsTable)
            {
                errors.EnsureError(DocumentErrorSeverity.Severe, args[0], ErrNeedValidVariableName_Arg, Name);
                return false;
            }

            DType retType = dataSourceType.IsError ? DType.EmptyRecord : dataSourceType.ToRecord();

            foreach (var assocDS in dataSourceType.AssociatedDataSources)
            {
                retType = DType.AttachDataSourceInfo(retType, assocDS);
            }

            for (var i = 1; i < args.Length; i++)
            {
                DType curType = argTypes[i];
                bool isSafeToUnion = true;

                if (!curType.IsRecord)
                {
                    errors.EnsureError(args[i], TexlStrings.ErrNeedRecord);
                    isValid = false;
                    continue;
                }

                // Checks if all record names exist against table type and if its possible to coerce.
                bool checkAggregateNames = curType.CheckAggregateNames(dataSourceType, args[i], errors, context.Features, SupportsParamCoercion);

                isValid = isValid && checkAggregateNames;
                isSafeToUnion = checkAggregateNames;

                if (isValid && SupportsParamCoercion && !dataSourceType.Accepts(curType, exact: true, useLegacyDateTimeAccepts: false, usePowerFxV1CompatibilityRules: context.Features.PowerFxV1CompatibilityRules))
                {
                    if (!curType.TryGetCoercionSubType(dataSourceType, out DType coercionType, out var coercionNeeded, context.Features))
                    {
                        isValid = false;
                    }
                    else
                    {
                        if (coercionNeeded)
                        {
                            CollectionUtils.Add(ref nodeToCoercedTypeMap, args[i], coercionType);
                        }

                        retType = DType.Union(retType, coercionType, useLegacyDateTimeAccepts: false, context.Features);
                    }
                }
                else if (isSafeToUnion)
                {
                    retType = DType.Union(retType, curType, useLegacyDateTimeAccepts: false, context.Features);
                }
            }

            returnType = retType;
            return isValid;
        }

        public override void CheckSemantics(TexlBinding binding, TexlNode[] args, DType[] argTypes, IErrorContainer errors)
        {
            base.CheckSemantics(binding, args, argTypes, errors);
            base.ValidateArgumentIsMutable(binding, args[0], errors);

            int skip = 2;

            MutationUtils.CheckForReadOnlyFields(argTypes[0], args.Skip(skip).ToArray(), argTypes.Skip(skip).ToArray(), errors);
        }

        public async Task<FormulaValue> InvokeAsync(FormulaValue[] args, CancellationToken cancellationToken)
        {
            var validArgs = CheckArgs(args, out FormulaValue faultyArg);

            if (!validArgs)
            {
                return faultyArg;
            }

            var arg0lazy = (LambdaFormulaValue)args[0];
            var arg0 = await arg0lazy.EvalAsync().ConfigureAwait(false);
            var arg1 = args[1];

            if (arg0 is BlankValue)
            {
                return arg0;
            }
            else if (arg0 is ErrorValue)
            {
                return arg0;
            }

            if (arg1 is BlankValue)
            {
                return arg1;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var changeRecord = FieldDictToRecordValue(await CreateRecordFromArgsDictAsync(args, 2, cancellationToken).ConfigureAwait(false));

            var datasource = (TableValue)arg0;
            var baseRecord = (RecordValue)arg1;

            cancellationToken.ThrowIfCancellationRequested();
            var ret = await datasource.UpdateAsync(baseRecord, changeRecord, cancellationToken).ConfigureAwait(false);

            return ret.ToFormulaValue();
        }
    }
}
