/*
  Copyright (C) 2014 Jeroen Frijters

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.

  Jeroen Frijters
  jeroen@frijters.net
  
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
#if STATIC_COMPILER
using IKVM.Reflection;
using IKVM.Reflection.Emit;
using Type = IKVM.Reflection.Type;
#else
using System.Reflection;
using System.Reflection.Emit;
#endif

namespace IKVM.Internal
{
	static class LambdaMetafactory
	{
		internal static bool Emit(DynamicTypeWrapper.FinishContext context, ClassFile classFile, ClassFile.ConstantPoolItemInvokeDynamic cpi, CodeEmitter ilgen)
		{
			ClassFile.BootstrapMethod bsm = classFile.GetBootstrapMethod(cpi.BootstrapMethod);
			if (!IsLambdaMetafactory(classFile, bsm) && !IsLambdaAltMetafactory(classFile, bsm))
			{
				return false;
			}
			if (!EmitImpl(context, classFile, cpi, bsm, ilgen))
			{
#if STATIC_COMPILER
				if (context.TypeWrapper.GetClassLoader().DisableDynamicBinding)
				{
					StaticCompiler.IssueMessage(Message.UnableToCreateLambdaFactory);
				}
#endif
				return false;
			}
			return true;
		}

		private static bool EmitImpl(DynamicTypeWrapper.FinishContext context, ClassFile classFile, ClassFile.ConstantPoolItemInvokeDynamic cpi, ClassFile.BootstrapMethod bsm, CodeEmitter ilgen)
		{
			if (HasUnloadable(cpi))
			{
				Fail("cpi has unloadable");
				return false;
			}
			bool serializable = false;
			if (bsm.ArgumentCount > 3)
			{
				AltFlags flags = (AltFlags)classFile.GetConstantPoolConstantInteger(bsm.GetArgument(3));
				serializable = (flags & AltFlags.Serializable) != 0;
				if ((flags & AltFlags.Markers) != 0)
				{
					Fail("markers");
					return false;
				}
				if ((flags & AltFlags.Bridges) != 0)
				{
					int bridgeCount = classFile.GetConstantPoolConstantInteger(bsm.GetArgument(4));
					if (bridgeCount != 0)
					{
						Fail("bridges");
						return false;
					}
				}
			}
			ClassFile.ConstantPoolItemMethodType samMethodType = classFile.GetConstantPoolConstantMethodType(bsm.GetArgument(0));
			ClassFile.ConstantPoolItemMethodHandle implMethod = classFile.GetConstantPoolConstantMethodHandle(bsm.GetArgument(1));
			ClassFile.ConstantPoolItemMethodType instantiatedMethodType = classFile.GetConstantPoolConstantMethodType(bsm.GetArgument(2));
			if (HasUnloadable(samMethodType)
				|| HasUnloadable((ClassFile.ConstantPoolItemMI)implMethod.MemberConstantPoolItem)
				|| HasUnloadable(instantiatedMethodType))
			{
				Fail("bsm args has unloadable");
				return false;
			}
			TypeWrapper interfaceType = cpi.GetRetType();
			MethodWrapper[] methodList;
			if (!IsSupportedInterface(interfaceType, out methodList))
			{
				Fail("interfaceType " + interfaceType.Name);
				return false;
			}
			if (!interfaceType.IsAccessibleFrom(context.TypeWrapper))
			{
				Fail("interfaceType not accessible");
				return false;
			}
			if (serializable && Array.Exists(methodList, delegate(MethodWrapper mw) { return mw.Name == "writeReplace" && mw.Signature == "()Ljava.lang.Object;"; }))
			{
				Fail("writeReplace");
				return false;
			}
			if (!IsSupportedImplMethod(implMethod, context.TypeWrapper, cpi.GetArgTypes(), instantiatedMethodType))
			{
				Fail("implMethod " + implMethod.MemberConstantPoolItem.Class + "::" + implMethod.MemberConstantPoolItem.Name + implMethod.MemberConstantPoolItem.Signature);
				return false;
			}
			if (!IsSubTypeOf(instantiatedMethodType, samMethodType))
			{
				Fail("instantiatedMethodType <= samMethodType");
				return false;
			}
			TypeWrapper[] implParameters = GetImplParameters(implMethod);
			if (cpi.GetArgTypes().Length + samMethodType.GetArgTypes().Length != implParameters.Length)
			{
				Fail("K + N = M");
				return false;
			}
			for (int i = 0, K = cpi.GetArgTypes().Length; i < K; i++)
			{
				if (!cpi.GetArgTypes()[i].IsSubTypeOf(implParameters[i]))
				{
					Fail("For i=1..K, Di = Ai");
					return false;
				}
			}
			for (int i = 0, N = samMethodType.GetArgTypes().Length, k = cpi.GetArgTypes().Length; i < N; i++)
			{
				if (!IsAdaptable(instantiatedMethodType.GetArgTypes()[i], implParameters[i + k], false))
				{
					Fail("For i=1..N, Ti is adaptable to Aj, where j=i+k (in" + context.TypeWrapper.Name + ")");
					return false;
				}
			}
			if (instantiatedMethodType.GetRetType() != PrimitiveTypeWrapper.VOID)
			{
				TypeWrapper Rt = instantiatedMethodType.GetRetType();
				TypeWrapper Ra = GetImplReturnType(implMethod);
				if (Ra == PrimitiveTypeWrapper.VOID || !IsAdaptable(Ra, Rt, true))
				{
					Fail("The return type Rt is void, or the return type Ra is not void and is adaptable to Rt");
					Console.WriteLine("Ra = " + Ra.SigName);
					Console.WriteLine("Rt = " + Rt.SigName);
					return false;
				}
			}
			MethodWrapper interfaceMethod = null;
			foreach (MethodWrapper mw in methodList)
			{
				if (mw.Name == cpi.Name && mw.Signature == samMethodType.Signature)
				{
					interfaceMethod = mw;
					break;
				}
			}
			if (interfaceMethod == null || !interfaceMethod.IsAbstract || IsObjectMethod(interfaceMethod) || !MatchSignatures(interfaceMethod, samMethodType))
			{
				Fail("interfaceMethod");
				return false;
			}

			TypeBuilder tb = context.DefineAnonymousClass();
			// we're not implementing the interfaces recursively (because we don't care about .NET Compact anymore),
			// but should we decide to do that, we'd need to somehow communicate to AnonymousTypeWrapper what the 'real' interface is
			tb.AddInterfaceImplementation(interfaceType.TypeAsBaseType);
			if (serializable)
			{
				tb.AddInterfaceImplementation(CoreClasses.java.io.Serializable.Wrapper.TypeAsBaseType);
			}
			MethodBuilder ctor = CreateConstructorAndDispatch(context, cpi.GetArgTypes(), tb, interfaceMethod, implParameters, samMethodType, implMethod, instantiatedMethodType, serializable);
			AddDefaultInterfaceMethods(context, methodList, tb);
			ilgen.Emit(OpCodes.Newobj, ctor);
			// the CLR verification rules about type merging mean we have to explicitly cast to the interface type here
			ilgen.Emit(OpCodes.Castclass, interfaceType.TypeAsBaseType);
			return true;
		}

		[Conditional("TRACE_LAMBDA_METAFACTORY")]
		private static void Fail(string msg)
		{
			Console.WriteLine("Fail: " + msg);
		}

		private static TypeWrapper[] GetImplParameters(ClassFile.ConstantPoolItemMethodHandle implMethod)
		{
			MethodWrapper mw = (MethodWrapper)implMethod.Member;
			TypeWrapper[] parameters = mw.GetParameters();
			if (mw.IsStatic || mw.IsConstructor)
			{
				return parameters;
			}
			return ArrayUtil.Concat(mw.DeclaringType, parameters);
		}

		private static TypeWrapper GetImplReturnType(ClassFile.ConstantPoolItemMethodHandle implMethod)
		{
			return implMethod.Kind == ClassFile.RefKind.newInvokeSpecial
				? implMethod.Member.DeclaringType
				: ((MethodWrapper)implMethod.Member).ReturnType;
		}

		private static bool IsAdaptable(TypeWrapper Q, TypeWrapper S, bool isReturn)
		{
			if (Q == S)
			{
				return true;
			}

			if (Q.IsPrimitive)
			{
				if (S.IsPrimitive)
				{
					// Q can be converted to S via a primitive widening conversion
					switch (Q.SigName[0] | S.SigName[0] << 8)
					{
						case 'B' | 'S' << 8:
						case 'B' | 'I' << 8:
						case 'B' | 'J' << 8:
						case 'B' | 'F' << 8:
						case 'B' | 'D' << 8:
						case 'S' | 'I' << 8:
						case 'S' | 'J' << 8:
						case 'S' | 'F' << 8:
						case 'S' | 'D' << 8:
						case 'C' | 'I' << 8:
						case 'C' | 'J' << 8:
						case 'C' | 'F' << 8:
						case 'C' | 'D' << 8:
						case 'I' | 'J' << 8:
						case 'I' | 'F' << 8:
						case 'I' | 'D' << 8:
						case 'J' | 'F' << 8:
						case 'J' | 'D' << 8:
						case 'F' | 'D' << 8:
							return true;
						default:
							return false;
					}
				}
				else
				{
					// S is a supertype of the Wrapper(Q)
					return GetWrapper(Q).IsAssignableTo(S);
				}
			}
			else if (isReturn)
			{
				return true;
			}
			else
			{
				if (S.IsPrimitive)
				{
					// If Q is a primitive wrapper, check that Primitive(Q) can be widened to S
					TypeWrapper primitive = GetPrimitiveFromWrapper(Q);
					return primitive != null && IsAdaptable(primitive, S, isReturn);
				}
				else
				{
					// for parameter types: S is a supertype of Q
					return Q.IsAssignableTo(S);
				}
			}
		}

		private static TypeWrapper GetWrapper(TypeWrapper primitive)
		{
			Debug.Assert(primitive.IsPrimitive);
			switch (primitive.SigName[0])
			{
				case 'Z':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Boolean");
				case 'B':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Byte");
				case 'S':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Short");
				case 'C':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Character");
				case 'I':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Integer");
				case 'J':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Long");
				case 'F':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Float");
				case 'D':
					return ClassLoaderWrapper.LoadClassCritical("java.lang.Double");
				default:
					throw new InvalidOperationException();
			}
		}

		private static TypeWrapper GetPrimitiveFromWrapper(TypeWrapper wrapper)
		{
			switch (wrapper.Name)
			{
				case "java.lang.Boolean":
					return PrimitiveTypeWrapper.BOOLEAN;
				case "java.lang.Byte":
					return PrimitiveTypeWrapper.BYTE;
				case "java.lang.Short":
					return PrimitiveTypeWrapper.SHORT;
				case "java.lang.Character":
					return PrimitiveTypeWrapper.CHAR;
				case "java.lang.Integer":
					return PrimitiveTypeWrapper.INT;
				case "java.lang.Long":
					return PrimitiveTypeWrapper.LONG;
				case "java.lang.Float":
					return PrimitiveTypeWrapper.FLOAT;
				case "java.lang.Double":
					return PrimitiveTypeWrapper.DOUBLE;
				default:
					return null;
			}
		}

		private static bool IsSubTypeOf(ClassFile.ConstantPoolItemMethodType instantiatedMethodType, ClassFile.ConstantPoolItemMethodType samMethodType)
		{
			TypeWrapper[] T = instantiatedMethodType.GetArgTypes();
			TypeWrapper[] U = samMethodType.GetArgTypes();
			if (T.Length != U.Length)
			{
				return false;
			}
			for (int i = 0; i < T.Length; i++)
			{
				if (!T[i].IsAssignableTo(U[i]))
				{
					return false;
				}
			}
			TypeWrapper Rt = instantiatedMethodType.GetRetType();
			TypeWrapper Ru = samMethodType.GetRetType();
			return Rt.IsAssignableTo(Ru);
		}

		private static MethodBuilder CreateConstructorAndDispatch(DynamicTypeWrapper.FinishContext context, TypeWrapper[] args, TypeBuilder tb,
			MethodWrapper interfaceMethod, TypeWrapper[] implParameters, ClassFile.ConstantPoolItemMethodType samMethodType, ClassFile.ConstantPoolItemMethodHandle implMethod,
			ClassFile.ConstantPoolItemMethodType instantiatedMethodType, bool serializable)
		{
			// captured values
			Type[] capturedTypes = new Type[args.Length];
			FieldBuilder[] capturedFields = new FieldBuilder[capturedTypes.Length];
			for (int i = 0; i < capturedTypes.Length; i++)
			{
				capturedTypes[i] = args[i].TypeAsSignatureType;
				FieldAttributes attr = FieldAttributes.Private;
				if (i > 0 || !args[0].IsGhost)
				{
					attr |= FieldAttributes.InitOnly;
				}
				capturedFields[i] = tb.DefineField("c" + i, capturedTypes[i], attr);
			}

			// constructor
			MethodBuilder ctor = ReflectUtil.DefineConstructor(tb, MethodAttributes.Assembly, capturedTypes);
			CodeEmitter ilgen = CodeEmitter.Create(ctor);
			ilgen.Emit(OpCodes.Ldarg_0);
			ilgen.Emit(OpCodes.Call, Types.Object.GetConstructor(Type.EmptyTypes));
			for (int i = 0; i < capturedTypes.Length; i++)
			{
				ilgen.EmitLdarg(0);
				ilgen.EmitLdarg(i + 1);
				ilgen.Emit(OpCodes.Stfld, capturedFields[i]);
			}
			ilgen.Emit(OpCodes.Ret);
			ilgen.DoEmit();

			// dispatch method
			MethodBuilder mb = interfaceMethod.GetDefineMethodHelper().DefineMethod(context.TypeWrapper, tb, interfaceMethod.RealName, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final);
			ilgen = CodeEmitter.Create(mb);
			for (int i = 0; i < capturedTypes.Length; i++)
			{
				ilgen.EmitLdarg(0);
				OpCode opc = OpCodes.Ldfld;
				if (i == 0 && args[0].IsGhost)
				{
					switch (implMethod.Kind)
					{
						case ClassFile.RefKind.invokeInterface:
						case ClassFile.RefKind.invokeVirtual:
						case ClassFile.RefKind.invokeSpecial:
							opc = OpCodes.Ldflda;
							break;
					}
				}
				ilgen.Emit(opc, capturedFields[i]);
			}
			for (int i = 0, count = interfaceMethod.GetParameters().Length, k = capturedFields.Length; i < count; i++)
			{
				ilgen.EmitLdarg(i + 1);
				TypeWrapper Ui = samMethodType.GetArgTypes()[i];
				TypeWrapper Ti = instantiatedMethodType.GetArgTypes()[i];
				TypeWrapper Aj = implParameters[i + k];
				if (Ui == PrimitiveTypeWrapper.BYTE)
				{
					ilgen.Emit(OpCodes.Conv_I1);
				}
				if (Ti != Ui)
				{
					if (Ti.IsGhost)
					{
						Ti.EmitConvStackTypeToSignatureType(ilgen, Ui);
					}
					else if (Ui.IsGhost)
					{
						Ui.EmitConvSignatureTypeToStackType(ilgen);
					}
					else
					{
						Ti.EmitCheckcast(ilgen);
					}
				}
				if (Ti != Aj)
				{
					if (Ti.IsPrimitive && !Aj.IsPrimitive)
					{
						Boxer.EmitBox(ilgen, Ti);
					}
					else if (!Ti.IsPrimitive && Aj.IsPrimitive)
					{
						TypeWrapper primitive = GetPrimitiveFromWrapper(Ti);
						Boxer.EmitUnbox(ilgen, primitive, false);
						if (primitive == PrimitiveTypeWrapper.BYTE)
						{
							ilgen.Emit(OpCodes.Conv_I1);
						}
					}
					else if (Aj == PrimitiveTypeWrapper.LONG)
					{
						ilgen.Emit(OpCodes.Conv_I8);
					}
					else if (Aj == PrimitiveTypeWrapper.FLOAT)
					{
						ilgen.Emit(OpCodes.Conv_R4);
					}
					else if (Aj == PrimitiveTypeWrapper.DOUBLE)
					{
						ilgen.Emit(OpCodes.Conv_R8);
					}
				}
			}
			switch (implMethod.Kind)
			{
				case ClassFile.RefKind.invokeVirtual:
				case ClassFile.RefKind.invokeInterface:
					((MethodWrapper)implMethod.Member).EmitCallvirt(ilgen);
					break;
				case ClassFile.RefKind.newInvokeSpecial:
					((MethodWrapper)implMethod.Member).EmitNewobj(ilgen);
					break;
				case ClassFile.RefKind.invokeStatic:
				case ClassFile.RefKind.invokeSpecial:
					((MethodWrapper)implMethod.Member).EmitCall(ilgen);
					break;
				default:
					throw new InvalidOperationException();
			}
			TypeWrapper Ru = interfaceMethod.ReturnType;
			TypeWrapper Ra = GetImplReturnType(implMethod);
			TypeWrapper Rt = instantiatedMethodType.GetRetType();
			if (Ra == PrimitiveTypeWrapper.BYTE)
			{
				ilgen.Emit(OpCodes.Conv_I1);
			}
			if (Ra != Ru)
			{
				if (Ru == PrimitiveTypeWrapper.VOID)
				{
					ilgen.Emit(OpCodes.Pop);
				}
				else if (Ra.IsGhost)
				{
					Ra.EmitConvSignatureTypeToStackType(ilgen);
				}
				else if (Ru.IsGhost)
				{
					Ru.EmitConvStackTypeToSignatureType(ilgen, Ra);
				}
			}
			if (Ra != Rt)
			{
				if (Rt.IsPrimitive)
				{
					if (Rt == PrimitiveTypeWrapper.VOID)
					{
						// already popped
					}
					else if (!Ra.IsPrimitive)
					{
						TypeWrapper primitive = GetPrimitiveFromWrapper(Ra);
						if (primitive != null)
						{
							Boxer.EmitUnbox(ilgen, primitive, false);
						}
						else
						{
							// If Q is not a primitive wrapper, cast Q to the base Wrapper(S); for example Number for numeric types
							EmitConvertingUnbox(ilgen, Rt);
						}
					}
					else if (Rt == PrimitiveTypeWrapper.LONG)
					{
						ilgen.Emit(OpCodes.Conv_I8);
					}
					else if (Rt == PrimitiveTypeWrapper.FLOAT)
					{
						ilgen.Emit(OpCodes.Conv_R4);
					}
					else if (Rt == PrimitiveTypeWrapper.DOUBLE)
					{
						ilgen.Emit(OpCodes.Conv_R8);
					}
				}
				else if (Ra.IsPrimitive)
				{
					Boxer.EmitBox(ilgen, GetPrimitiveFromWrapper(Rt));
				}
				else
				{
					Rt.EmitCheckcast(ilgen);
				}
			}
			ilgen.EmitTailCallPrevention();
			ilgen.Emit(OpCodes.Ret);
			ilgen.DoEmit();

			// writeReplace method
			if (serializable)
			{
				MethodBuilder writeReplace = tb.DefineMethod("writeReplace", MethodAttributes.Private, Types.Object, Type.EmptyTypes);
				ilgen = CodeEmitter.Create(writeReplace);
				context.TypeWrapper.EmitClassLiteral(ilgen);
				ilgen.Emit(OpCodes.Ldstr, interfaceMethod.DeclaringType.Name.Replace('.', '/'));
				ilgen.Emit(OpCodes.Ldstr, interfaceMethod.Name);
				ilgen.Emit(OpCodes.Ldstr, interfaceMethod.Signature.Replace('.', '/'));
				ilgen.EmitLdc_I4((int)implMethod.Kind);
				ilgen.Emit(OpCodes.Ldstr, implMethod.Class.Replace('.', '/'));
				ilgen.Emit(OpCodes.Ldstr, implMethod.Name);
				ilgen.Emit(OpCodes.Ldstr, implMethod.Signature.Replace('.', '/'));
				ilgen.Emit(OpCodes.Ldstr, instantiatedMethodType.Signature.Replace('.', '/'));
				ilgen.EmitLdc_I4(capturedFields.Length);
				ilgen.Emit(OpCodes.Newarr, Types.Object);
				for (int i = 0; i < capturedFields.Length; i++)
				{
					ilgen.Emit(OpCodes.Dup);
					ilgen.EmitLdc_I4(i);
					ilgen.EmitLdarg(0);
					ilgen.Emit(OpCodes.Ldfld, capturedFields[i]);
					if (args[i].IsPrimitive)
					{
						Boxer.EmitBox(ilgen, args[i]);
					}
					else if (args[i].IsGhost)
					{
						args[i].EmitConvSignatureTypeToStackType(ilgen);
					}
					ilgen.Emit(OpCodes.Stelem, Types.Object);
				}
				MethodWrapper ctorSerializedLambda = ClassLoaderWrapper.LoadClassCritical("java.lang.invoke.SerializedLambda").GetMethodWrapper(StringConstants.INIT,
					"(Ljava.lang.Class;Ljava.lang.String;Ljava.lang.String;Ljava.lang.String;ILjava.lang.String;Ljava.lang.String;Ljava.lang.String;Ljava.lang.String;[Ljava.lang.Object;)V", false);
				ctorSerializedLambda.Link();
				ctorSerializedLambda.EmitNewobj(ilgen);
				ilgen.Emit(OpCodes.Ret);
				ilgen.DoEmit();
			}

			return ctor;
		}

		private static void EmitConvertingUnbox(CodeEmitter ilgen, TypeWrapper tw)
		{
			switch (tw.SigName[0])
			{
				case 'Z':
				case 'C':
					Boxer.EmitUnbox(ilgen, tw, true);
					break;
				case 'B':
					EmitUnboxNumber(ilgen, "byteValue", "()B");
					break;
				case 'S':
					EmitUnboxNumber(ilgen, "shortValue", "()S");
					break;
				case 'I':
					EmitUnboxNumber(ilgen, "intValue", "()I");
					break;
				case 'J':
					EmitUnboxNumber(ilgen, "longValue", "()J");
					break;
				case 'F':
					EmitUnboxNumber(ilgen, "floatValue", "()F");
					break;
				case 'D':
					EmitUnboxNumber(ilgen, "doubleValue", "()D");
					break;
				default:
					throw new InvalidOperationException();
			}
		}

		private static void EmitUnboxNumber(CodeEmitter ilgen, string methodName, string methodSig)
		{
			TypeWrapper tw = ClassLoaderWrapper.LoadClassCritical("java.lang.Number");
			tw.EmitCheckcast(ilgen);
			MethodWrapper mw = tw.GetMethodWrapper(methodName, methodSig, false);
			mw.Link();
			mw.EmitCallvirt(ilgen);
		}

		private static void AddDefaultInterfaceMethods(DynamicTypeWrapper.FinishContext context, MethodWrapper[] methodList, TypeBuilder tb)
		{
			TypeWrapperFactory factory = context.TypeWrapper.GetClassLoader().GetTypeWrapperFactory();
			foreach (MethodWrapper mw in methodList)
			{
				if (!mw.IsAbstract)
				{
					MethodBuilder mb = mw.GetDefineMethodHelper().DefineMethod(factory, tb, mw.RealName, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final);
					DynamicTypeWrapper.FinishContext.EmitCallDefaultInterfaceMethod(mb, mw);
				}
				else if (IsObjectMethod(mw))
				{
					MethodBuilder mb = mw.GetDefineMethodHelper().DefineMethod(factory, tb, mw.RealName, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final);
					CodeEmitter ilgen = CodeEmitter.Create(mb);
					for (int i = 0, count = mw.GetParameters().Length; i <= count; i++)
					{
						ilgen.EmitLdarg(i);
					}
					CoreClasses.java.lang.Object.Wrapper.GetMethodWrapper(mw.Name, mw.Signature, false).EmitCallvirt(ilgen);
					ilgen.Emit(OpCodes.Ret);
					ilgen.DoEmit();
				}
			}
		}

		private static bool IsSupportedImplMethod(ClassFile.ConstantPoolItemMethodHandle implMethod, TypeWrapper caller, TypeWrapper[] captured, ClassFile.ConstantPoolItemMethodType instantiatedMethodType)
		{
			switch (implMethod.Kind)
			{
				case ClassFile.RefKind.invokeVirtual:
				case ClassFile.RefKind.invokeInterface:
				case ClassFile.RefKind.newInvokeSpecial:
				case ClassFile.RefKind.invokeStatic:
				case ClassFile.RefKind.invokeSpecial:
					break;
				default:
					return false;
			}
			MethodWrapper mw = (MethodWrapper)implMethod.Member;
			if (mw == null || mw.HasCallerID || DynamicTypeWrapper.RequiresDynamicReflectionCallerClass(mw.DeclaringType.Name, mw.Name, mw.Signature))
			{
				return false;
			}
			TypeWrapper instance;
			if (mw.IsConstructor)
			{
				instance = mw.DeclaringType;
			}
			else if (mw.IsStatic)
			{
				instance = null;
			}
			else
			{
				// if implMethod is an instance method, the type of the first captured value must be subtype of implMethod.DeclaringType
				instance = captured.Length == 0 ? instantiatedMethodType.GetArgTypes()[0] : captured[0];
				if (!instance.IsAssignableTo(mw.DeclaringType))
				{
					return false;
				}
			}
			if (!mw.IsAccessibleFrom(mw.DeclaringType, caller, instance))
			{
				return false;
			}
			mw.Link();
			return true;
		}

		private static bool IsSupportedInterface(TypeWrapper tw, out MethodWrapper[] methodList)
		{
			// we don't need to check for unloadable, because we already did that while validating the invoke signature
			if (!tw.IsInterface || tw.IsGhost)
			{
				methodList = null;
				return false;
			}
			Dictionary<MethodKey, MethodWrapper> methods = new Dictionary<MethodKey,MethodWrapper>();
			int abstractMethodCount = 0;
			if (GatherAllInterfaceMethods(tw, methods, ref abstractMethodCount) && abstractMethodCount == 1)
			{
				methodList = new MethodWrapper[methods.Count];
				methods.Values.CopyTo(methodList, 0);
				return true;
			}
			methodList = null;
			return false;
		}

		private static bool GatherAllInterfaceMethods(TypeWrapper tw, Dictionary<MethodKey, MethodWrapper> methods, ref int abstractMethodCount)
		{
			foreach (MethodWrapper mw in tw.GetMethods())
			{
				if (mw.IsVirtual)
				{
					MirandaMethodWrapper mmw = mw as MirandaMethodWrapper;
					if (mmw != null)
					{
						if (mmw.Error != null)
						{
							return false;
						}
						continue;
					}
					MethodKey key = new MethodKey("", mw.Name, mw.Signature);
					MethodWrapper current;
					if (methods.TryGetValue(key, out current))
					{
						if (!MatchSignatures(mw, current))
						{
							// linkage error (or unloadable type)
							return false;
						}
					}
					else
					{
						methods.Add(key, mw);
						if (mw.IsAbstract && !IsObjectMethod(mw))
						{
							abstractMethodCount++;
						}
					}
					mw.Link();
					if (mw.GetMethod() == null)
					{
						return false;
					}
				}
			}
			foreach (TypeWrapper tw1 in tw.Interfaces)
			{
				if (!GatherAllInterfaceMethods(tw1, methods, ref abstractMethodCount))
				{
					return false;
				}
			}
			return true;
		}

		private static bool IsObjectMethod(MethodWrapper mw)
		{
			MethodWrapper objectMethod;
			return (objectMethod = CoreClasses.java.lang.Object.Wrapper.GetMethodWrapper(mw.Name, mw.Signature, false)) != null
				&& objectMethod.IsPublic;
		}

		private static bool MatchSignatures(MethodWrapper interfaceMethod, ClassFile.ConstantPoolItemMethodType samMethodType)
		{
			return interfaceMethod.ReturnType == samMethodType.GetRetType()
				&& MatchTypes(interfaceMethod.GetParameters(), samMethodType.GetArgTypes());
		}

		private static bool MatchSignatures(MethodWrapper mw1, MethodWrapper mw2)
		{
			return mw1.ReturnType == mw2.ReturnType
				&& MatchTypes(mw1.GetParameters(), mw2.GetParameters());
		}

		private static bool MatchTypes(TypeWrapper[] ar1, TypeWrapper[] ar2)
		{
			if (ar1.Length != ar2.Length)
			{
				return false;
			}
			for (int i = 0; i < ar1.Length; i++)
			{
				if (ar1[i] != ar2[i])
				{
					return false;
				}
			}
			return true;
		}

		private static bool IsLambdaMetafactory(ClassFile classFile, ClassFile.BootstrapMethod bsm)
		{
			ClassFile.ConstantPoolItemMethodHandle mh;
			return bsm.ArgumentCount == 3
				&& classFile.GetConstantPoolConstantType(bsm.GetArgument(0)) == ClassFile.ConstantType.MethodType
				&& classFile.GetConstantPoolConstantType(bsm.GetArgument(1)) == ClassFile.ConstantType.MethodHandle
				&& classFile.GetConstantPoolConstantType(bsm.GetArgument(2)) == ClassFile.ConstantType.MethodType
				&& (mh = classFile.GetConstantPoolConstantMethodHandle(bsm.BootstrapMethodIndex)).Kind == ClassFile.RefKind.invokeStatic
				&& IsLambdaMetafactory(mh.Member);
		}

		private static bool IsLambdaMetafactory(MemberWrapper mw)
		{
			return mw.Name == "metafactory"
				&& mw.Signature == "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.invoke.MethodType;Ljava.lang.invoke.MethodType;Ljava.lang.invoke.MethodHandle;Ljava.lang.invoke.MethodType;)Ljava.lang.invoke.CallSite;"
				&& mw.DeclaringType.Name == "java.lang.invoke.LambdaMetafactory";
		}

		[Flags]
		enum AltFlags
		{
			Serializable = 1,
			Markers = 2,
			Bridges = 4,
			Mask = Serializable | Markers | Bridges
		}

		private static bool IsLambdaAltMetafactory(ClassFile classFile, ClassFile.BootstrapMethod bsm)
		{
			ClassFile.ConstantPoolItemMethodHandle mh;
			AltFlags flags;
			int argpos = 4;
			return bsm.ArgumentCount >= 4
				&& (mh = classFile.GetConstantPoolConstantMethodHandle(bsm.BootstrapMethodIndex)).Kind == ClassFile.RefKind.invokeStatic
				&& IsLambdaAltMetafactory(mh.Member)
				&& classFile.GetConstantPoolConstantType(bsm.GetArgument(0)) == ClassFile.ConstantType.MethodType
				&& classFile.GetConstantPoolConstantType(bsm.GetArgument(1)) == ClassFile.ConstantType.MethodHandle
				&& classFile.GetConstantPoolConstantType(bsm.GetArgument(2)) == ClassFile.ConstantType.MethodType
				&& classFile.GetConstantPoolConstantType(bsm.GetArgument(3)) == ClassFile.ConstantType.Integer
				&& ((flags = (AltFlags)classFile.GetConstantPoolConstantInteger(bsm.GetArgument(3))) & ~AltFlags.Mask) == 0
				&& ((flags & AltFlags.Markers) == 0 || CheckOptionalArgs(classFile, bsm, ClassFile.ConstantType.Class, ref argpos))
				&& ((flags & AltFlags.Bridges) == 0 || CheckOptionalArgs(classFile, bsm, ClassFile.ConstantType.MethodType, ref argpos))
				&& argpos == bsm.ArgumentCount;
		}

		private static bool IsLambdaAltMetafactory(MemberWrapper mw)
		{
			return mw.Name == "altMetafactory"
				&& mw.Signature == "(Ljava.lang.invoke.MethodHandles$Lookup;Ljava.lang.String;Ljava.lang.invoke.MethodType;[Ljava.lang.Object;)Ljava.lang.invoke.CallSite;"
				&& mw.DeclaringType.Name == "java.lang.invoke.LambdaMetafactory";
		}

		private static bool CheckOptionalArgs(ClassFile classFile, ClassFile.BootstrapMethod bsm, ClassFile.ConstantType type, ref int argpos)
		{
			if (bsm.ArgumentCount - argpos < 1)
			{
				return false;
			}
			if (classFile.GetConstantPoolConstantType(bsm.GetArgument(argpos)) != ClassFile.ConstantType.Integer)
			{
				return false;
			}
			int count = classFile.GetConstantPoolConstantInteger(bsm.GetArgument(argpos++));
			if (count < 0 || bsm.ArgumentCount - argpos < count)
			{
				return false;
			}
			for (int i = 0; i < count; i++)
			{
				if (classFile.GetConstantPoolConstantType(bsm.GetArgument(argpos++)) != type)
				{
					return false;
				}
			}
			return true;
		}

		private static bool HasUnloadable(ClassFile.ConstantPoolItemInvokeDynamic cpi)
		{
			return HasUnloadable(cpi.GetArgTypes()) || cpi.GetRetType().IsUnloadable;
		}

		private static bool HasUnloadable(ClassFile.ConstantPoolItemMethodType cpi)
		{
			return HasUnloadable(cpi.GetArgTypes()) || cpi.GetRetType().IsUnloadable;
		}

		private static bool HasUnloadable(ClassFile.ConstantPoolItemMI cpi)
		{
			return HasUnloadable(cpi.GetArgTypes()) || cpi.GetRetType().IsUnloadable;
		}

		private static bool HasUnloadable(TypeWrapper[] wrappers)
		{
			foreach (TypeWrapper tw in wrappers)
			{
				if (tw.IsUnloadable)
				{
					return true;
				}
			}
			return false;
		}
	}
}