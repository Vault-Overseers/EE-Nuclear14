using Robust.Shared.Physics;
using Content.Shared.Damage;
using Content.Shared.Explosion;
using Content.Shared.Clothing.Components;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Markings;
using Content.Server.Humanoid;
using Content.Shared.Inventory.Events;
using Content.Shared.Tag;
using Content.Shared.Teleportation.Components;
using Content.Shared.Storage.Components;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using Robust.Shared.Physics.Components;
using System.Numerics;
using Content.Shared.DeltaV.Lamiae;
using Robust.Shared.Physics.Events;
using Content.Shared.Projectiles;
using Content.Shared.Weapons.Ranged.Events;
using System.Linq;

namespace Content.Server.DeltaV.Lamiae
{
    public sealed partial class LamiaSystem : EntitySystem
    {
        [Dependency] private readonly SharedJointSystem _jointSystem = default!;
        [Dependency] private readonly HumanoidAppearanceSystem _humanoid = default!;
        [Dependency] private readonly DamageableSystem _damageableSystem = default!;
        [Dependency] private readonly TagSystem _tagSystem = default!;
        [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

        [ValidatePrototypeId<TagPrototype>]
        private const string LamiaHardsuitTag = "AllowLamiaHardsuit";

        Queue<(LamiaSegmentComponent segment, EntityUid lamia)> _segments = new();
        public override void Update(float frameTime)
        {
            base.Update(frameTime);
            foreach (var segment in _segments)
            {
                var segmentUid = segment.segment.Owner;
                var attachedUid = segment.segment.AttachedToUid;
                if (!Exists(segmentUid) || !Exists(attachedUid)
                || MetaData(segmentUid).EntityLifeStage > EntityLifeStage.MapInitialized
                || MetaData(attachedUid).EntityLifeStage > EntityLifeStage.MapInitialized
                || Transform(segmentUid).MapID == MapId.Nullspace
                || Transform(attachedUid).MapID == MapId.Nullspace)
                    continue;

                EnsureComp<PhysicsComponent>(segmentUid);
                EnsureComp<PhysicsComponent>(attachedUid); // Hello I hate tests

                var ev = new SegmentSpawnedEvent(segment.lamia);
                RaiseLocalEvent(segmentUid, ev, false);

                if (segment.segment.SegmentNumber == 1)
                {
                    Transform(segmentUid).Coordinates = Transform(attachedUid).Coordinates;
                    var revoluteJoint = _jointSystem.CreateWeldJoint(attachedUid, segmentUid, id: "Segment" + segment.segment.SegmentNumber + segment.segment.Lamia);
                    revoluteJoint.CollideConnected = false;
                }
                if (segment.segment.SegmentNumber <= segment.segment.MaxSegments)
                    Transform(segmentUid).Coordinates = Transform(attachedUid).Coordinates.Offset(new Vector2(0, segment.segment.OffsetSwitching));
                else
                    Transform(segmentUid).Coordinates = Transform(attachedUid).Coordinates.Offset(new Vector2(0, segment.segment.OffsetSwitching));

                var joint = _jointSystem.CreateDistanceJoint(attachedUid, segmentUid, id: ("Segment" + segment.segment.SegmentNumber + segment.segment.Lamia));
                joint.CollideConnected = false;
                joint.Stiffness = 0.2f;
            }
            _segments.Clear();
        }
        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<LamiaComponent, ComponentInit>(OnInit);
            SubscribeLocalEvent<LamiaComponent, ComponentShutdown>(OnShutdown);
            SubscribeLocalEvent<LamiaComponent, JointRemovedEvent>(OnJointRemoved);
            SubscribeLocalEvent<LamiaComponent, EntGotRemovedFromContainerMessage>(OnRemovedFromContainer);
            SubscribeLocalEvent<LamiaComponent, HitScanAfterRayCastEvent>(OnShootHitscan);
            SubscribeLocalEvent<LamiaSegmentComponent, SegmentSpawnedEvent>(OnSegmentSpawned);
            SubscribeLocalEvent<LamiaSegmentComponent, DamageChangedEvent>(HandleDamageTransfer);
            SubscribeLocalEvent<LamiaSegmentComponent, DamageModifyEvent>(HandleSegmentDamage);
            SubscribeLocalEvent<LamiaComponent, InsertIntoEntityStorageAttemptEvent>(OnLamiaStorageInsertAttempt);
            SubscribeLocalEvent<LamiaSegmentComponent, InsertIntoEntityStorageAttemptEvent>(OnSegmentStorageInsertAttempt);
            SubscribeLocalEvent<LamiaComponent, DidEquipEvent>(OnDidEquipEvent);
            SubscribeLocalEvent<LamiaComponent, DidUnequipEvent>(OnDidUnequipEvent);
            SubscribeLocalEvent<LamiaSegmentComponent, GetExplosionResistanceEvent>(OnSnekBoom);
            SubscribeLocalEvent<LamiaSegmentComponent, PreventCollideEvent>(PreventShootSelf);
        }

        /// <summary>
        /// Handles transferring marking selections to the tail segments. Every tail marking must be repeated 2 times in order for this script to work.
        /// </summary>
        /// <param name="uid"></param>
        /// <param name="component"></param>
        /// <param name="args"></param>
        // TODO: Please for the love of god don't make me write a test to validate that every marking also has its matching segment states.
        // Future contributors will just find out when their game crashes because they didn't make a marking-segment.
        private void OnSegmentSpawned(EntityUid uid, LamiaSegmentComponent component, SegmentSpawnedEvent args)
        {
            component.Lamia = args.Lamia;

            if (!TryComp<HumanoidAppearanceComponent>(uid, out var species)) return;
            if (!TryComp<HumanoidAppearanceComponent>(args.Lamia, out var humanoid)) return;
            if (!TryComp<AppearanceComponent>(uid, out var appearance)) return;

            _appearance.SetData(uid, ScaleVisuals.Scale, component.ScaleFactor, appearance);

            if (humanoid.MarkingSet.TryGetCategory(MarkingCategories.Tail, out var tailMarkings))
            {
                foreach (var markings in tailMarkings)
                {
                    var segmentId = species.Species;
                    var markingId = markings.MarkingId;
                    string segmentmarking = $"{markingId}-{segmentId}";
                    _humanoid.AddMarking(uid, segmentmarking, markings.MarkingColors);
                }
            }
        }

        private void OnInit(EntityUid uid, LamiaComponent component, ComponentInit args)
        {
            Math.Clamp(component.NumberOfSegments, 2, 18);
            Math.Clamp(component.TaperOffset, 1, component.NumberOfSegments - 1);
            SpawnSegments(uid, component);
        }

        private void OnShutdown(EntityUid uid, LamiaComponent component, ComponentShutdown args)
        {
            foreach (var segment in component.Segments)
            {
                QueueDel(segment);
            }

            component.Segments.Clear();
        }

        private void OnJointRemoved(EntityUid uid, LamiaComponent component, JointRemovedEvent args)
        {
            if (!component.Segments.Contains(args.OtherEntity))
                return;

            foreach (var segment in component.Segments)
                QueueDel(segment);

            component.Segments.Clear();
        }

        private void OnRemovedFromContainer(EntityUid uid, LamiaComponent component, EntGotRemovedFromContainerMessage args)
        {
            if (component.Segments.Count != 0)
            {
                foreach (var segment in component.Segments)
                    QueueDel(segment);
                component.Segments.Clear();
            }

            SpawnSegments(uid, component);
        }

        private void HandleSegmentDamage(EntityUid uid, LamiaSegmentComponent component, DamageModifyEvent args)
        {
            if (args.Origin == component.Lamia)
                args.Damage *= 0;
            args.Damage = args.Damage / component.DamageModifyFactor;
        }
        private void HandleDamageTransfer(EntityUid uid, LamiaSegmentComponent component, DamageChangedEvent args)
        {
            if (args.DamageDelta == null) return;
            _damageableSystem.TryChangeDamage(component.Lamia, args.DamageDelta);
        }

        public void SpawnSegments(EntityUid uid, LamiaComponent component)
        {
            int i = 1;
            var addTo = uid;
            while (i <= component.NumberOfSegments + 1)
            {
                var segment = AddSegment(addTo, uid, component, i);
                addTo = segment;
                i++;
            }
        }

        private EntityUid AddSegment(EntityUid segmentuid, EntityUid parentuid, LamiaComponent lamiaComponent, int segmentNumber)
        {
            LamiaSegmentComponent segmentComponent = new();
            segmentComponent.Lamia = parentuid;
            segmentComponent.AttachedToUid = segmentuid;
            segmentComponent.DamageModifierConstant = lamiaComponent.NumberOfSegments * lamiaComponent.DamageModifierOffset;
            float damageModifyCoefficient = segmentComponent.DamageModifierConstant / lamiaComponent.NumberOfSegments;
            segmentComponent.DamageModifyFactor = segmentComponent.DamageModifierConstant * damageModifyCoefficient;
            segmentComponent.ExplosiveModifyFactor = 1 / segmentComponent.DamageModifyFactor / (lamiaComponent.NumberOfSegments * lamiaComponent.ExplosiveModifierOffset);

            float taperConstant = lamiaComponent.NumberOfSegments - lamiaComponent.TaperOffset;
            EntityUid segment;
            if (segmentNumber == 1)
                segment = EntityManager.SpawnEntity(lamiaComponent.InitialSegmentId, Transform(segmentuid).Coordinates);
            else
                segment = EntityManager.SpawnEntity(lamiaComponent.SegmentId, Transform(segmentuid).Coordinates);
            if (segmentNumber >= taperConstant && lamiaComponent.UseTaperSystem == true)
            {
                segmentComponent.OffsetSwitching = lamiaComponent.StaticOffset * MathF.Pow(lamiaComponent.OffsetConstant, segmentNumber - taperConstant);
                segmentComponent.ScaleFactor = lamiaComponent.StaticScale * MathF.Pow(1f / lamiaComponent.OffsetConstant, segmentNumber - taperConstant);
            }
            else
            {
                segmentComponent.OffsetSwitching = lamiaComponent.StaticOffset;
                segmentComponent.ScaleFactor = lamiaComponent.StaticScale;
            }
            if (segmentNumber % 2 != 0)
            {
                segmentComponent.OffsetSwitching *= -1;
            }

            segmentComponent.Owner = segment;
            segmentComponent.SegmentNumber = segmentNumber;
            EntityManager.AddComponent(segment, segmentComponent, true);
            EnsureComp<PortalExemptComponent>(segment);
            _segments.Enqueue((segmentComponent, parentuid));
            lamiaComponent.Segments.Add(segment);
            return segment;
        }

        private void OnLamiaStorageInsertAttempt(EntityUid uid, LamiaComponent comp, ref InsertIntoEntityStorageAttemptEvent args)
        {
            args.Cancelled = true;
        }

        private void OnSegmentStorageInsertAttempt(EntityUid uid, LamiaSegmentComponent comp, ref InsertIntoEntityStorageAttemptEvent args)
        {
            args.Cancelled = true;
        }

        private void OnDidEquipEvent(EntityUid equipee, LamiaComponent component, DidEquipEvent args)
        {
            if (!TryComp<ClothingComponent>(args.Equipment, out var clothing)) return;
            if (args.Slot == "outerClothing" && _tagSystem.HasTag(args.Equipment, LamiaHardsuitTag))
            {
                foreach (var uid in component.Segments)
                {
                    if (!TryComp<AppearanceComponent>(uid, out var appearance)) return;
                    _appearance.SetData(uid, LamiaSegmentVisualLayers.Armor, true, appearance);
                    if (clothing.RsiPath == null) return;
                    _appearance.SetData(uid, LamiaSegmentVisualLayers.ArmorRsi, clothing.RsiPath, appearance);
                }
            }
        }

        private void OnSnekBoom(EntityUid uid, LamiaSegmentComponent component, ref GetExplosionResistanceEvent args)
        {
            args.DamageCoefficient = component.ExplosiveModifyFactor;
        }

        private void OnDidUnequipEvent(EntityUid equipee, LamiaComponent component, DidUnequipEvent args)
        {
            if (args.Slot == "outerClothing" && _tagSystem.HasTag(args.Equipment, LamiaHardsuitTag))
            {
                foreach (var uid in component.Segments)
                {
                    if (!TryComp<AppearanceComponent>(uid, out var appearance)) return;
                    _appearance.SetData(uid, LamiaSegmentVisualLayers.Armor, false, appearance);
                }
            }
        }

        private void PreventShootSelf(EntityUid uid, LamiaSegmentComponent component, ref PreventCollideEvent args)
        {
            if (!TryComp<ProjectileComponent>(args.OtherEntity, out var projectileComponent)) return;

            if (projectileComponent.Shooter == component.Lamia)
            {
                args.Cancelled = true;
            }
        }

        private void OnShootHitscan(EntityUid uid, LamiaComponent component, ref HitScanAfterRayCastEvent args)
        {
            if (args.RayCastResults == null) return;

            var entityList = new List<RayCastResults>();
            foreach (var entity in args.RayCastResults)
            {
                if (!component.Segments.Contains(entity.HitEntity))
                    entityList.Add(entity);
            }
            args.RayCastResults = entityList;
        }
    }
}
