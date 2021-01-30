﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Confuser.Core;
using Confuser.Renamer.References;
using Confuser.Renamer.Services;
using dnlib.DotNet;

namespace Confuser.Renamer.Analyzers {
	public class VTableAnalyzer : IRenamer {
		void IRenamer.Analyze(IConfuserContext context, INameService service, IProtectionParameters parameters, IDnlibDef def) {
			if (service is NameService nameService) {
				switch (def) {
					case TypeDef typeDef:
						Analyze(context, nameService, typeDef);
						break;
					case MethodDef methodDef:
						Analyze(context, nameService, methodDef);
						break;
				}
			}
		}

		internal static void Analyze(IConfuserContext context, NameService service, TypeDef type) {
			if (type.IsInterface)
				return;

			var vTbl = service.GetVTables()[type];
			foreach (var ifaceVTbl in vTbl.InterfaceSlots.Values) {
				foreach (var slot in ifaceVTbl) {
					if (slot.Overrides == null)
						continue;
					Debug.Assert(slot.Overrides.MethodDef.DeclaringType.IsInterface);
					// A method in base type can implements an interface method for a
					// derived type. If the base type/interface is not in our control, we should
					// not rename the methods.
					bool baseUnderCtrl = context.Modules.Contains(slot.MethodDef.DeclaringType.Module as ModuleDefMD);
					bool ifaceUnderCtrl = context.Modules.Contains(slot.Overrides.MethodDef.DeclaringType.Module as ModuleDefMD);
					if ((!baseUnderCtrl && ifaceUnderCtrl) || !service.CanRename(context, slot.MethodDef)) {
						service.SetCanRename(context, slot.Overrides.MethodDef, false);
					}
					else if (baseUnderCtrl && !ifaceUnderCtrl || !service.CanRename(context, slot.Overrides.MethodDef)) {
						service.SetCanRename(context, slot.MethodDef, false);
					}
				}
			}
		}

		internal static void Analyze(IConfuserContext context, NameService service, MethodDef method) {
			if (!method.IsVirtual)
				return;

			var vTbl = service.GetVTables()[method.DeclaringType];
			var slots = vTbl.FindSlots(method).ToArray();

			IMemberDef discoveredBaseMemberDef = null;
			MethodDef discoveredBaseMethodDef = null;

			bool doesOverridePropertyOrEvent = false;
			var methodProp = method.DeclaringType.Properties.Where(p => BelongsToProperty(p, method));
			foreach (var prop in methodProp) {
				foreach (var baseMethodDef in FindBaseDeclarations(service, method)) {
					var basePropDef = baseMethodDef.DeclaringType.Properties.
						FirstOrDefault(p => BelongsToProperty(p, baseMethodDef) && String.Equals(p.Name, prop.Name, StringComparison.Ordinal));

					if (basePropDef is null) continue;

					// Name of property has to line up.
					CreateOverrideReference(context, service, prop, basePropDef);
					CreateSiblingReference(basePropDef, ref discoveredBaseMemberDef, context, service);

					// Method names have to line up as well (otherwise inheriting attributes does not work).
					CreateOverrideReference(context, service, method, baseMethodDef);
					CreateSiblingReference(baseMethodDef, ref discoveredBaseMethodDef, context, service);

					doesOverridePropertyOrEvent = true;
				}
			}

			discoveredBaseMemberDef = null;
			discoveredBaseMethodDef = null;

			var methodEvent = method.DeclaringType.Events.Where(e => BelongsToEvent(e, method));
			foreach (var evt in methodEvent) {
				foreach (var baseMethodDef in FindBaseDeclarations(service, method)) {
					var baseEventDef = baseMethodDef.DeclaringType.Events.
						FirstOrDefault(e => BelongsToEvent(e, baseMethodDef) && String.Equals(e.Name, evt.Name, StringComparison.Ordinal));

					if (baseEventDef is null) continue;

					// Name of event has to line up.
					CreateOverrideReference(context, service, evt, baseEventDef);
					CreateSiblingReference(baseEventDef, ref discoveredBaseMemberDef, context, service);

					// Method names have to line up as well (otherwise inheriting attributes does not work).
					CreateOverrideReference(context, service, method, baseMethodDef);
					CreateSiblingReference(baseMethodDef, ref discoveredBaseMethodDef, context, service);

					doesOverridePropertyOrEvent = true;
				}
			}

			if (!method.IsAbstract) {
				foreach (var slot in slots) {
					if (slot.Overrides == null)
						continue;

					SetupOverwriteReferences(context, service, slot, method.Module);
				}
			}
			else if (!doesOverridePropertyOrEvent) {
				foreach (var baseMethodDef in FindBaseDeclarations(service, method)) {
					CreateOverrideReference(context, service, method, baseMethodDef);
				}
			}
		}

		private static void CreateSiblingReference<T>(T basePropDef, ref T discoveredBaseMemberDef, IConfuserContext context, INameService service) where T : class, IMemberDef {
			if (discoveredBaseMemberDef is null)
				discoveredBaseMemberDef = basePropDef;
			else {
				var references = service.GetReferences(context, discoveredBaseMemberDef).OfType<MemberSiblingReference>().ToArray();
				if (references.Length > 0) {
					discoveredBaseMemberDef = (T)references[0].OldestSiblingDef;
					foreach (var siblingRef in references.Skip(1)) {
						// Redirect all the siblings to the new oldest reference
						RedirectSiblingReferences(siblingRef.OldestSiblingDef, discoveredBaseMemberDef, context, service);
					}
				}

				service.AddReference(context, basePropDef, new MemberSiblingReference(basePropDef, discoveredBaseMemberDef));
				UpdateOldestSiblingReference(discoveredBaseMemberDef, basePropDef, context, service);
			}
		}

		private static void UpdateOldestSiblingReference(IMemberDef oldestSiblingMemberDef, IMemberDef basePropDef, IConfuserContext context, INameService service) {
			var reverseReference = service.GetReferences(context, oldestSiblingMemberDef).OfType<MemberOldestSiblingReference>()
				.SingleOrDefault();
			if (reverseReference is null) {
				service.AddReference(context, oldestSiblingMemberDef, new MemberOldestSiblingReference(oldestSiblingMemberDef, basePropDef));
				PropagateRenamingRestrictions(context, service, oldestSiblingMemberDef, basePropDef);
			}
			else if (!reverseReference.OtherSiblings.Contains(basePropDef)) {
				reverseReference.OtherSiblings.Add(basePropDef);
				PropagateRenamingRestrictions(context, service, reverseReference.OtherSiblings);
			}
		}

		private static void RedirectSiblingReferences(IMemberDef oldMemberDef, IMemberDef newMemberDef, IConfuserContext context, INameService service) {
			if (ReferenceEquals(oldMemberDef, newMemberDef)) return;

			var referencesToUpdate = service.GetReferences(context, oldMemberDef)
				.OfType<MemberOldestSiblingReference>()
				.SelectMany(r => r.OtherSiblings)
				.SelectMany(def => service.GetReferences(context, def))
				.OfType<MemberSiblingReference>()
				.Where(r => ReferenceEquals(r.OldestSiblingDef, oldMemberDef));

			foreach (var reference in referencesToUpdate) {
				reference.OldestSiblingDef = newMemberDef;
				UpdateOldestSiblingReference(newMemberDef, reference.ThisMemberDef, context, service);
			}
			UpdateOldestSiblingReference(newMemberDef, oldMemberDef, context, service);
		}

		private static void CreateOverrideReference(IConfuserContext context, INameService service, IMemberDef thisMemberDef, IMemberDef baseMemberDef) {
			var overrideRef = new MemberOverrideReference(thisMemberDef, baseMemberDef);
			service.AddReference(context, thisMemberDef, overrideRef);

			PropagateRenamingRestrictions(context, service, thisMemberDef, baseMemberDef);
		}

		private static void PropagateRenamingRestrictions<T>(IConfuserContext context, INameService service, params T[] objects) where T : IDnlibDef =>
			PropagateRenamingRestrictions(context, service, (IList<T>)objects);

		private static void PropagateRenamingRestrictions<T>(IConfuserContext context, INameService service, IList<T> objects) where T : IDnlibDef {
			if (!objects.All(o => service.CanRename(context, o))) {
				foreach (var o in objects) {
					service.SetCanRename(context, o, false);
				}
			}
			else {
				var minimalRenamingLevel = objects.Max(o => service.GetRenameMode(context, o));
				foreach (var o in objects) {
					service.ReduceRenameMode(context, o, minimalRenamingLevel);
				}
			}
		}

		private static IEnumerable<MethodDef> FindBaseDeclarations(NameService service, MethodDef method) {
			var unprocessed = new Queue<MethodDef>();
			unprocessed.Enqueue(method);

			var vTables = service.GetVTables();

			while (unprocessed.Any()) {
				var currentMethod = unprocessed.Dequeue();

				var vTbl = vTables[currentMethod.DeclaringType];
				var slots = vTbl.FindSlots(currentMethod).Where(s => s.Overrides != null);

				bool slotsExists = false;
				foreach (var slot in slots) {
					unprocessed.Enqueue(slot.Overrides.MethodDef);
					slotsExists = true;
				}
				
				if (!slotsExists && method != currentMethod)
					yield return currentMethod;
			}
		}

		private static bool BelongsToProperty(PropertyDef propertyDef, MethodDef methodDef) =>
			propertyDef.GetMethods.Contains(methodDef) || propertyDef.SetMethods.Contains(methodDef) ||
			(propertyDef.HasOtherMethods && propertyDef.OtherMethods.Contains(methodDef));

		private static bool BelongsToEvent(EventDef eventDef, MethodDef methodDef) =>
			Equals(eventDef.AddMethod, methodDef) || Equals(eventDef.RemoveMethod, methodDef) || Equals(eventDef.InvokeMethod, methodDef) ||
			(eventDef.HasOtherMethods && eventDef.OtherMethods.Contains(methodDef));

		private static void AddImportReference(IConfuserContext context, INameService service, ModuleDef module, MethodDef method, MemberRef methodRef) {
			if (method.Module != module && context.Modules.Contains((ModuleDefMD)module)) {
				var declType = (TypeRef)methodRef.DeclaringType.ScopeType;
				service.AddReference(context, method.DeclaringType, new TypeRefReference(declType, method.DeclaringType));
				service.AddReference(context, method, new MemberRefReference(methodRef, method));

				var typeRefs = methodRef.MethodSig.Params.SelectMany(param => param.FindTypeRefs()).ToList();
				typeRefs.AddRange(methodRef.MethodSig.RetType.FindTypeRefs());
				typeRefs.AddRange(methodRef.DeclaringType.ToTypeSig().FindTypeRefs());
				foreach (var typeRef in typeRefs) {
					SetupTypeReference(context, service, module, typeRef);
				}
			}
		}

		private static void SetupTypeReference(IConfuserContext context, INameService service, ModuleDef module, ITypeDefOrRef typeDefOrRef) {
			if (!(typeDefOrRef is TypeRef typeRef)) return;

			var def = typeRef.ResolveTypeDefThrow();
			if (def.Module != module && context.Modules.Contains((ModuleDefMD)def.Module))
				service.AddReference(context, def, new TypeRefReference(typeRef, def));
		}

		private static GenericInstSig SetupSignatureReferences(IConfuserContext context, INameService service, ModuleDef module, GenericInstSig typeSig) {
			var genericType = SetupSignatureReferences(context, service, module, typeSig.GenericType);
			var genericArguments = typeSig.GenericArguments.Select(a => SetupSignatureReferences(context, service, module, a)).ToList();
			return new GenericInstSig(genericType, genericArguments);
		}

		private static T SetupSignatureReferences<T>(IConfuserContext context, INameService service, ModuleDef module, T typeSig) where T : TypeSig {
			var asTypeRef = typeSig.TryGetTypeRef();
			if (asTypeRef != null) {
				SetupTypeReference(context, service, module, asTypeRef);
			}
			return typeSig;
		}

		private static void SetupOverwriteReferences(IConfuserContext context, INameService service, VTableSlot slot, ModuleDef module) {
			var methodDef = slot.MethodDef;
			var baseSlot = slot.Overrides;
			var baseMethodDef = baseSlot.MethodDef;

			var overrideRef = new OverrideDirectiveReference(slot, baseSlot);
			service.AddReference(context, methodDef, overrideRef);
			service.AddReference(context, slot.Overrides.MethodDef, overrideRef);

			var importer = new Importer(module, ImporterOptions.TryToUseTypeDefs);

			IMethodDefOrRef target;
			if (baseSlot.MethodDefDeclType is GenericInstSig declType) {
				var signature = SetupSignatureReferences(context, service, module, declType);
				MemberRef targetRef = new MemberRefUser(module, baseMethodDef.Name, baseMethodDef.MethodSig, signature.ToTypeDefOrRef());
				targetRef = importer.Import(targetRef);
				service.AddReference(context, baseMethodDef, new MemberRefReference(targetRef, baseMethodDef));

				target = targetRef;
			}
			else {
				target = baseMethodDef;
				if (target.Module != module) {
					target = (IMethodDefOrRef)importer.Import(baseMethodDef);
					if (target is MemberRef memberRef)
						service.AddReference(context, baseMethodDef, new MemberRefReference(memberRef, baseMethodDef));
				}
			}

			target.MethodSig = importer.Import(target.MethodSig);
			if (target is MemberRef methodRef)
				AddImportReference(context, service, module, baseMethodDef, methodRef);

			if (methodDef.Overrides.Any(impl => IsMatchingOverride(impl, target)))
				return;

			methodDef.Overrides.Add(new MethodOverride(methodDef, target));
		}

		private static bool IsMatchingOverride(MethodOverride methodOverride, IMethodDefOrRef targetMethod) {
			SigComparer comparer = default;

			var targetDeclTypeDef = targetMethod.DeclaringType.ResolveTypeDef();
			var overrideDeclTypeDef = methodOverride.MethodDeclaration.DeclaringType.ResolveTypeDef();
			if (!comparer.Equals(targetDeclTypeDef, overrideDeclTypeDef))
				return false;

			var targetMethodSig = targetMethod.MethodSig;
			var overrideMethodSig = methodOverride.MethodDeclaration.MethodSig;
			if (methodOverride.MethodDeclaration.DeclaringType is TypeSpec spec && spec.TypeSig is GenericInstSig genericInstSig) {
				overrideMethodSig = GenericArgumentResolver.Resolve(overrideMethodSig, genericInstSig.GenericArguments);
			}

			return comparer.Equals(targetMethodSig, overrideMethodSig);
		}

		public void PreRename(IConfuserContext context, INameService service, IProtectionParameters parameters,
			IDnlibDef def) {
			//
		}

		public void PostRename(IConfuserContext context, INameService service, IProtectionParameters parameters,
			IDnlibDef def) {
			if (!(def is MethodDef method) || !method.IsVirtual || method.Overrides.Count == 0)
				return;

			method.Overrides
				  .RemoveWhere(impl => MethodEqualityComparer.CompareDeclaringTypes.Equals(impl.MethodDeclaration, method));
		}
	}
}
