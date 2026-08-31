using System;
using GTA;
using GTA.Native;

namespace FlockSurveillance
{
    /// <summary>
    /// Reads the newest weapon-damage cause for a camera destruction and
    /// classifies whether Photo Lab should also synthesize an explosion.
    /// </summary>
    internal static class SurveillanceExplosiveWeapon
    {
        private const int ExplosiveDamageType = 5;
        private const int MaximumDamageRecordAgeMilliseconds = 1000;

        private static readonly WeaponHash[] WeaponHashes =
        {
            WeaponHash.RPG,
            WeaponHash.HomingLauncher,
            WeaponHash.GrenadeLauncher,
            WeaponHash.CompactGrenadeLauncher,
            WeaponHash.Grenade,
            WeaponHash.StickyBomb,
            WeaponHash.ProximityMine,
            WeaponHash.PipeBomb,
            WeaponHash.Firework,
            WeaponHash.Railgun,
            WeaponHash.RailgunXmas3,
            WeaponHash.CompactEMPLauncher
        };

        private static readonly WeaponHash[] AllWeaponHashes =
            (WeaponHash[])Enum.GetValues(typeof(WeaponHash));

        public static bool TryFindLatestDamage(
            Entity damagedEntity,
            out WeaponHash weaponHash,
            out bool isExplosive
        )
        {
            weaponHash = default(WeaponHash);
            isExplosive = false;

            if (damagedEntity == null)
            {
                return false;
            }

            try
            {
                if (!damagedEntity.Exists())
                {
                    return false;
                }

                EntityDamageRecord[] records =
                    damagedEntity.DamageRecords.GetAllDamageRecords();
                int currentGameTime = Game.GameTime;
                int newestAge = int.MaxValue;
                bool foundNewestRecord = false;

                if (records != null)
                {
                    foreach (EntityDamageRecord record in records)
                    {
                        uint rawWeaponHash = (uint)record.WeaponHash;
                        int age = unchecked(
                            currentGameTime - record.GameTime
                        );

                        if (
                            rawWeaponHash == 0u ||
                            age < 0 ||
                            age > MaximumDamageRecordAgeMilliseconds ||
                            age > newestAge
                        )
                        {
                            continue;
                        }

                        bool recordIsExplosive = Function.Call<int>(
                            Hash.GET_WEAPON_DAMAGE_TYPE,
                            rawWeaponHash
                        ) == ExplosiveDamageType;

                        if (age < newestAge)
                        {
                            newestAge = age;
                            foundNewestRecord = true;
                            isExplosive = recordIsExplosive;
                            weaponHash = record.WeaponHash;
                        }
                        else if (recordIsExplosive)
                        {
                            // Multiple records can share a game-time stamp.
                            // Preserve an explosive cause on an exact tie.
                            isExplosive = true;
                            weaponHash = record.WeaponHash;
                        }
                    }
                }

                if (foundNewestRecord)
                {
                    return true;
                }
            }
            catch
            {
                // Fall back to the stable public damage flags below. This
                // keeps camera destruction working if the optional damage
                // record view is unavailable in a particular GTA build.
            }

            try
            {
                WeaponHash currentWeapon =
                    Game.Player.Character.Weapons.Current.Hash;

                if (
                    currentWeapon != WeaponHash.Unarmed &&
                    damagedEntity.HasBeenDamagedBy(currentWeapon)
                )
                {
                    weaponHash = currentWeapon;
                    isExplosive = IsExplosive(currentWeapon);
                    return true;
                }

                foreach (WeaponHash candidate in AllWeaponHashes)
                {
                    if (
                        candidate != WeaponHash.Unarmed &&
                        damagedEntity.HasBeenDamagedBy(candidate)
                    )
                    {
                        weaponHash = candidate;
                        isExplosive = IsExplosive(candidate);
                        return true;
                    }
                }
            }
            catch
            {
                weaponHash = default(WeaponHash);
                isExplosive = false;
            }

            return false;
        }

        public static bool IsExplosive(WeaponHash weaponHash)
        {
            try
            {
                return Function.Call<int>(
                    Hash.GET_WEAPON_DAMAGE_TYPE,
                    (uint)weaponHash
                ) == ExplosiveDamageType;
            }
            catch
            {
                foreach (WeaponHash candidate in WeaponHashes)
                {
                    if (candidate == weaponHash)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        public static bool IsValidRecordedWeapon(
            int weaponHash,
            string weaponName
        )
        {
            if (weaponHash == 0 ||
                string.IsNullOrWhiteSpace(weaponName) ||
                weaponName.Length > 64)
            {
                return false;
            }

            uint expectedHash = unchecked((uint)weaponHash);
            uint numericHash;

            if (uint.TryParse(weaponName, out numericHash))
            {
                return numericHash == expectedHash;
            }

            WeaponHash parsed;

            return Enum.TryParse(weaponName, false, out parsed) &&
                (uint)parsed == expectedHash;
        }

        public static bool IsSupportedName(string weaponName)
        {
            if (string.IsNullOrWhiteSpace(weaponName) ||
                weaponName.Length > 64)
            {
                return false;
            }

            uint numericHash;

            if (uint.TryParse(weaponName, out numericHash))
            {
                // Undefined DLC/vehicle weapon hashes stringify as their
                // unsigned numeric value. The recorder only writes one after
                // GTA itself classifies the newest damage record as explosive.
                return numericHash != 0u;
            }

            WeaponHash parsed;

            if (!Enum.TryParse(weaponName, false, out parsed))
            {
                return false;
            }

            foreach (WeaponHash candidate in WeaponHashes)
            {
                if (candidate == parsed)
                {
                    return true;
                }
            }

            return false;
        }

        public static ExplosionType GetReplayExplosionType(
            string weaponName
        )
        {
            WeaponHash weaponHash;

            if (!Enum.TryParse(weaponName, false, out weaponHash))
            {
                return ExplosionType.Rocket;
            }

            switch (weaponHash)
            {
                case WeaponHash.Grenade:
                    return ExplosionType.Grenade;

                case WeaponHash.GrenadeLauncher:
                case WeaponHash.CompactGrenadeLauncher:
                    return ExplosionType.GrenadeL;

                case WeaponHash.StickyBomb:
                    return ExplosionType.StickyBomb;

                case WeaponHash.ProximityMine:
                    return ExplosionType.ProxMine;

                case WeaponHash.PipeBomb:
                    return ExplosionType.PipeBomb;

                case WeaponHash.Firework:
                    return ExplosionType.FireWork;

                case WeaponHash.Railgun:
                case WeaponHash.RailgunXmas3:
                    return ExplosionType.Railgun;

                case WeaponHash.CompactEMPLauncher:
                    return ExplosionType.EmpLauncherEmp;

                default:
                    return ExplosionType.Rocket;
            }
        }
    }

    internal sealed class SurveillanceCameraDestructionCause
    {
        private SurveillanceCameraDestructionCause()
        {
        }

        public bool DestroyedByWeapon { get; private set; }
        public int DestroyingWeaponHash { get; private set; }
        public string DestroyingWeaponName { get; private set; }
        public bool DestroyedByExplosiveWeapon { get; private set; }
        public string DestroyingExplosiveWeapon { get; private set; }

        public static SurveillanceCameraDestructionCause NonWeapon()
        {
            return new SurveillanceCameraDestructionCause();
        }

        public static SurveillanceCameraDestructionCause Weapon(
            WeaponHash? weaponHash,
            bool isExplosive
        )
        {
            string weaponName = weaponHash.HasValue
                ? weaponHash.Value.ToString()
                : null;

            return new SurveillanceCameraDestructionCause
            {
                DestroyedByWeapon = true,
                DestroyingWeaponHash = weaponHash.HasValue
                    ? unchecked((int)(uint)weaponHash.Value)
                    : 0,
                DestroyingWeaponName = weaponName,
                DestroyedByExplosiveWeapon =
                    weaponHash.HasValue && isExplosive,
                DestroyingExplosiveWeapon =
                    weaponHash.HasValue && isExplosive
                        ? weaponName
                        : null
            };
        }
    }
}
