using System;
using RimWorld;
using Verse;
using Verse.AI;
using Harmony;
using System.Reflection;
using System.Reflection.Emit; // for OpCodes in Harmony Transpiler
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static LWM.DeepStorage.Utils.DBF; // trace utils

/*   If a DSU is above capacity, the extra items should get moved */


namespace LWM.DeepStorage
{
/********************
 * Sometimes, items will end up in piles where they don't really belong.
 * Several scenarios:
 *  1. Pawn gets drafted and drops stuff into DSU cell
 *  2. DSU gets destroyed and leaves several things lying about,
 *       maybe some on top of one another.
 *  3. Maybe weight of an item changes somehow
 *  4. Maybe I made a mistake in the code and too many things 
 *       were put into a DSU.  It happens.
 *
 * Patch RimWorld's StoreUtility's TryFindBestBetterStoreCellFor
 *   Add a check to see if the item fits (maybe it's in a DSU but over capacity!).  
 *   If it doesn't (the DSU is overCapacity, then we need to look for a better 
 *   place for it!  So in the code, the test for storage priority must fail...
 *
 * In the code, right after
 *   StoragePriority storagePriority = currentPriority;
 * Add a test:
 *   bool overCapacity = Patch_Etc.OverCapacity(map, thing, ref storagePriority);
 *
 * If it IS overCapacity, then storagePriority is set to UnStored, so any valid
 *   storage is better.
 *
 * Then when vanilla checks if (...||  priority <= currentPriority), we change that
 * to if (...|| (!overCapacity && priority <= currentPriority)), so that if we are 
 * overCapacity, we continue to search for a better place to put it.
 *
 * I use Transpiler because it's right in the middle of code.  :p
 */
    [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterStoreCellFor")]
    static class Patch_TryFindBestBetterStoreCellFor {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,
                                                       ILGenerator generator) {
            List<CodeInstruction> code=instructions.ToList();
            /* First, we need to set up a local variable: */
            LocalBuilder overCapacity=generator.DeclareLocal(typeof(bool));
            int i=0;
            for (; i<code.Count;i++) {
                yield return code[i];
                if (code[i].opcode==OpCodes.Stloc_1){ // StoragePriority storagePriority = currentPriority;
                    yield return new CodeInstruction(OpCodes.Ldarg_2); // map
                    yield return new CodeInstruction(OpCodes.Ldarg_0); // thing
                    yield return new CodeInstruction(OpCodes.Ldloca_S,1); // ref to storagePriority
                    yield return new CodeInstruction(OpCodes.Call,
                                                     Harmony.AccessTools.Method(typeof(Patch_TryFindBestBetterStoreCellFor),"OverCapacity"));
                    yield return new CodeInstruction(OpCodes.Stloc_S, overCapacity);//overCapacty=OverCapacity(map,thing,ref storagePriority)
                    i++; // move past Stloc_1; we aleady returned that
                    break;
                }
            }
            if (i==code.Count) Log.Error("LWM.DeepStorage: TryFindBestBetterStoreCellFor transpile failed(0)");
            /* Replace:
              if (priority < storagePriority || priority <= currentPriority)
              {
                break;
              }
            * with
              if (priority < storagePriority || (!overCapacity && priority <= currentPriority))
            */
            for (;i<code.Count;i++) { // Go until find the storage priority for slotGroup (priority) and StLoc it:
                if (code[i].opcode==OpCodes.Stloc_S && ((LocalBuilder)code[i].operand).LocalIndex==7) {
                    break;
                }
                yield return code[i];
            }
            if (i==code.Count) Log.Error("LWM.DeepStorage: TryFindBestBetterStoreCellFor transpile failed(1)");
            // skip only a few lines, but I want to make sure I catch
            //     priority <= currentPriority, and this is easier than counting.xs
            for (;i<code.Count;i++) {
                if (code[i].opcode==OpCodes.Ldloc_S && ((LocalBuilder)code[i].operand).LocalIndex==7 &&
                    code[i+1].opcode==OpCodes.Ldarg_3 && code[i+2].opcode==OpCodes.Bgt) {
                    yield return new CodeInstruction(OpCodes.Ldloc_S,overCapacity);
                    yield return new CodeInstruction(OpCodes.Brtrue,code[i+2].operand);
                    break;
                }
                yield return code[i];
            }
            if (i==code.Count) Log.Error("LWM.DeepStorage: TryFindBestBetterStoreCellFor transpile failed(2)");
            // All done!
            for (;i<code.Count;i++)
                yield return code[i];
        }

        static bool OverCapacity(Map map, Thing thing, ref StoragePriority storagePriority) {
            if (!thing.Spawned) return false;
            if (storagePriority == StoragePriority.Unstored) return false;                
            CompDeepStorage cds = (thing.Position.GetSlotGroup(map)?.parent as ThingWithComps)?.GetComp<CompDeepStorage>();
            List<Thing> l;
            // What if it's a slotGroup put down after someone moved/destroyed a DSU?
            //   Because we CAN still end up with more than one thing on the ground.
            if (cds == null) {
                l=map.thingGrid.ThingsListAt(thing.Position);
                for (int i=0; i<l.Count;i++) {
                    if (l[i].def.EverStorable(false)) {
                        if (thing==l[i])
                            return false;
                        Utils.Warn(ShouldRemoveFromStorage,
                                   "LWM.DeepStorage: "+thing.stackCount+thing+
                                   " is not in a DSU and there is already a thing at "+thing.Position);
                        storagePriority=StoragePriority.Unstored;
                        return true;
                    }
                }
            }
            
            if (cds.limitingFactorForItem > 0f) {
                if (thing.GetStatValue(cds.stat) > cds.limitingFactorForItem) {
                    Utils.Warn(ShouldRemoveFromStorage,
                               "LWM.DeepStorage: "+thing.stackCount+thing+" is too heavy for DSU at "+thing.Position);
                    storagePriority=StoragePriority.Unstored;
                    return true;
                }
            }
            float totalWeightStoredHere=0f;  //mass, or bulk, etc.
            var stacksStoredHere=0;

            l=map.thingGrid.ThingsListAt(thing.Position);
            for (int i=0; i<l.Count;i++) {
                Thing thingInStorage=l[i];
                // TODO: Decide how to handle weight: if thing is stackable, do I want to take some out, if it goes over?
                if (thing == thingInStorage) return false; // it's here, and not over capacity yet!
                if (thingInStorage.def.EverStorable(false)) { // an "item" we care about
                    stacksStoredHere++;
                    if (cds.limitingTotalFactorForCell > 0f) {
                        totalWeightStoredHere+=thingInStorage.GetStatValue(cds.stat)*thingInStorage.stackCount;
                        if (totalWeightStoredHere > cds.limitingTotalFactorForCell &&
                            stacksStoredHere >= cds.minNumberStacks) {
                            Utils.Warn(ShouldRemoveFromStorage,
                                       "LWM.DeepStorage: "+thing.stackCount+thing+" is over weight capacity at "+thing.Position);
                            storagePriority=StoragePriority.Unstored;
                            return true;  // this takes us to capacity, and we haven't hit thing
                        }
                    }
                    if (stacksStoredHere >= cds.maxNumberStacks) { // breaks if minNumberStacks > maxNumberStacks. I'm okay with this
                        Utils.Warn(ShouldRemoveFromStorage,
                                   "LWM.DeepStorage: "+thing.stackCount+thing+" is over capacity at "+thing.Position);
                        storagePriority=StoragePriority.Unstored;
                        return true;  // this takes us to capacity, and we haven't hit thing
                    }
                } // if storable
            } // end list
            return false;
        }
    } // end patching TryFindBestBetterStoreCellFor
}
