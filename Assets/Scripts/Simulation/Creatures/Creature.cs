using UnityEngine;

namespace TapTap2026.Simulation
{
    /// <summary>
    /// 动物实体基类：连续浮点位置 + 贴地 AABB 碰撞体，随逻辑帧（20 Hz）推进。
    /// 相当于粒子体系里 <see cref="IParticleType"/> 的地位——物种差异全部由派生类的虚属性表达，
    /// <see cref="CreatureWorld"/> 只认识本基类，不认识具体物种。
    /// </summary>
    /// <remarks>
    /// <para><b>位置约定：</b><see cref="X"/> / <see cref="Y"/> 是碰撞盒**底边中点**（脚底中心），
    /// 而不是几何中心——脚下的格坐标可以直接用 <c>floor(Y)</c> 拿到，支撑 / 落地判定不必再减半高；
    /// 渲染层补上 <c>Height/2</c> 即可得到精灵中心（见 CreatureRenderer）。
    /// 碰撞盒跨度 <c>[X − Width/2, X + Width/2] × [Y, Y + Height]</c>，单位 = 格；
    /// 世界坐标与格坐标**同一套数值**（与渲染取景约定一致，无单位换算）。</para>
    /// <para><b>对世界只读：</b>物理路径（<see cref="StepPhysics"/>）只调用 <see cref="CreatureTerrain"/>
    /// 的查询，绝不写世界。未来的写行为（打洞 / 吃草）**必须**走
    /// <see cref="ParticleWorld.Set"/> / <see cref="ParticleWorld.Clear"/> / <see cref="ParticleWorld.SetPhase"/>，
    /// 让粒子侧看得见这次改动（否则世界账目会悄悄失衡）。</para>
    /// <para><b>不消耗世界随机流：</b>动物用自己的 <see cref="CreatureWorld.NextUInt"/>；
    /// 纯 C# 物理路径禁止 UnityEngine.Random 与 Time.*，也不做任何分配。</para>
    /// <para><b>时间约定：</b><see cref="VelocityX"/> / <see cref="VelocityY"/> 的单位是「格 / 逻辑帧」。
    /// <see cref="PrevX"/> / <see cref="PrevY"/> 由 <see cref="CreatureWorld.Step"/> 在每帧开头统一快照，
    /// 渲染层据此在两组位置之间插值。</para>
    /// </remarks>
    public abstract class Creature
    {
        /// <summary>覆盖列 / 行判定用的微小 epsilon：把「压在整格边界上」的情况稳定地归到内侧。</summary>
        private const float Epsilon = 1e-4f;

        /// <summary>当前位置 x（碰撞盒底边中点，格），为本逻辑帧步进后的值。</summary>
        public float X;

        /// <summary>当前位置 y（碰撞盒底边中点，格），为本逻辑帧步进后的值。</summary>
        public float Y;

        /// <summary>上一逻辑帧位置 x（渲染插值用；由 <see cref="CreatureWorld.Step"/> 开头统一快照）。</summary>
        public float PrevX;

        /// <summary>上一逻辑帧位置 y（渲染插值用；由 <see cref="CreatureWorld.Step"/> 开头统一快照）。</summary>
        public float PrevY;

        /// <summary>水平速度（格 / 逻辑帧）。</summary>
        public float VelocityX;

        /// <summary>竖直速度（格 / 逻辑帧），向上为正；受重力为负。</summary>
        public float VelocityY;

        /// <summary>朝向：+1 朝右、−1 朝左（渲染镜像用）。</summary>
        public int Facing;

        /// <summary>是否存活。由 <see cref="Kill"/> 置 false，在世界清扫阶段真正移除。</summary>
        public bool Alive;

        /// <summary>已存活的逻辑帧数（每推进一帧 +1，用于寿命 / 渲染节拍）。</summary>
        public int AgeTicks;

        /// <summary>本帧脚下是否有支撑（<see cref="StepPhysics"/> 收尾时刷新）。</summary>
        public bool IsGrounded;

        /// <summary>本帧身体中心（<c>X, Y + Height/2</c>）是否在水中（<see cref="StepPhysics"/> 收尾时刷新）。</summary>
        public bool IsSubmerged;

        /// <summary>碰撞盒宽（格）。构造时给定，之后不变。</summary>
        public readonly float Width;

        /// <summary>碰撞盒高（格）。构造时给定，之后不变。</summary>
        public readonly float Height;

        /// <summary>本实体被 <see cref="Kill"/> 标注的死因，仅供 <see cref="CreatureWorld"/> 清扫时读取。</summary>
        internal CreatureDeathCause PendingDeathCause;

        /// <summary>构造碰撞盒尺寸（格）给定的实体。</summary>
        /// <param name="width">碰撞盒宽（格），须为正。</param>
        /// <param name="height">碰撞盒高（格），须为正。</param>
        protected Creature(float width, float height)
        {
            Width = width;
            Height = height;
            Facing = 1;
            Alive = true;
        }

        /// <summary>物种重力加速度（格 / 逻辑帧²）。</summary>
        protected abstract float Gravity { get; }

        /// <summary>物种最大下落速率（格 / 逻辑帧），竖直速度的绝对值上限。</summary>
        protected abstract float MaxFallSpeed { get; }

        /// <summary>水中每帧净浮力（格 / 逻辑帧²；负 = 下沉）。默认 0，即水里只有阻力没有浮力。</summary>
        protected virtual float Buoyancy
        {
            get { return 0f; }
        }

        /// <summary>水中速度保持系数折损 ∈ [0, 1)；每逻辑帧把竖直速度乘以 <c>1 − WaterDrag</c>。默认 0.5。</summary>
        protected virtual float WaterDrag
        {
            get { return 0.5f; }
        }

        /// <summary>物种调试名（中文，日志 / 编辑器用）。</summary>
        public abstract string DebugName { get; }

        /// <summary>渲染占位色（本阶段没有美术资源，渲染层用纯色方块表示）。</summary>
        public abstract Color32 PlaceholderColor { get; }

        /// <summary>生成钩子：加入世界后、首个逻辑帧之前调用一次。本阶段默认空体。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        public virtual void OnSpawn(CreatureWorld creatures, ParticleWorld world)
        {
        }

        /// <summary>逻辑钩子：每逻辑帧、**物理之前**调用（决策 → 运动：写速度 / 朝向 / 行为）。本阶段默认空体。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        public virtual void OnLogic(CreatureWorld creatures, ParticleWorld world)
        {
        }

        /// <summary>死亡钩子：世界清扫本实体时、<see cref="CreatureWorld.CreatureDied"/> 之前调用。本阶段默认空体。</summary>
        /// <param name="creatures">所属动物世界。</param>
        /// <param name="world">粒子世界（只读）。</param>
        /// <param name="cause">死因。</param>
        public virtual void OnDeath(CreatureWorld creatures, ParticleWorld world, CreatureDeathCause cause)
        {
        }

        /// <summary>标注死亡（幂等：重复调用只有第一次生效，死因以第一次为准）。</summary>
        /// <remarks>
        /// 本方法**只置标记**，不从世界移除、不发事件——真正的移除与
        /// <see cref="CreatureWorld.CreatureDied"/> 由 <see cref="CreatureWorld.Step"/> 的清扫阶段统一做，
        /// 避免在遍历中间改动列表。
        /// </remarks>
        /// <param name="cause">死因。</param>
        public void Kill(CreatureDeathCause cause)
        {
            if (!Alive)
            {
                return;
            }

            Alive = false;
            PendingDeathCause = cause;
        }

        /// <summary>
        /// 一步物理：流体作用 → 竖直积分（逐行扫）→ 水平积分 → 世界边界夹取 → 刷新支撑 / 淹没状态。
        /// 轴分离 AABB，对世界**只读**。
        /// </summary>
        /// <remarks>
        /// <para><b>为什么轴分离：</b>先解竖直再解水平，每一步都退化成「沿一条轴扫一格宽的条带」，
        /// 判定简单、结果可预测，且天然允许「贴着墙下滑」「沿地面平移」这类滑行行为，
        /// 不会像对角射线那样需要处理法线分解。</para>
        /// <para><b>为什么逐行 / 逐列扫而不是只看落点：</b>单帧位移可能跨多格（未来可能有高速动物 /
        /// 小世界），只查落点会穿透薄地形。这里从旧边扫到新边，遇到的第一格实体就停住——
        /// 位移 &lt; 1 格时退化成一次检查，成本与朴素做法相同。</para>
        /// <para>全程 float 四则 + floor，无随机数、无时间、无分配。</para>
        /// </remarks>
        /// <param name="world">被查询的粒子世界（只读）。</param>
        internal void StepPhysics(ParticleWorld world)
        {
            // —— ① 流体作用：用**旧位置**的中心格判断是否在水中，再积分竖直速度 ——
            int oldCenterRow = FloorToInt(Y + Height * 0.5f);
            if (CreatureTerrain.IsWater(world, FloorToInt(X), oldCenterRow))
            {
                VelocityY = VelocityY * (1f - WaterDrag) + Buoyancy;
            }

            VelocityY -= Gravity;
            if (VelocityY < -MaxFallSpeed)
            {
                VelocityY = -MaxFallSpeed;
            }
            else if (VelocityY > MaxFallSpeed)
            {
                VelocityY = MaxFallSpeed;
            }

            // —— ② 竖直积分：从旧边逐行扫到新边（跨几行扫几行），撞上就停在格子外沿 ——
            float oldY = Y;
            float targetY = Y + VelocityY;
            int colMin = FloorToInt(X - Width * 0.5f + Epsilon);
            int colMax = FloorToInt(X + Width * 0.5f - Epsilon);

            if (VelocityY < 0f)
            {
                // 下行：底边进入的行 = floor(旧底边) − 1 递减到 floor(新底边)
                int rowFirst = FloorToInt(oldY) - 1;
                int rowLast = FloorToInt(targetY);
                for (int row = rowFirst; row >= rowLast; row--)
                {
                    if (AnySolidInRow(world, row, colMin, colMax))
                    {
                        targetY = row + 1;
                        VelocityY = 0f;
                        break;
                    }
                }
            }
            else if (VelocityY > 0f)
            {
                // 上行：头顶进入的行 = floor(旧顶边 − ε) + 1 递增到 floor(新顶边 − ε)
                int rowFirst = FloorToInt(oldY + Height - Epsilon) + 1;
                int rowLast = FloorToInt(targetY + Height - Epsilon);
                for (int row = rowFirst; row <= rowLast; row++)
                {
                    if (AnySolidInRow(world, row, colMin, colMax))
                    {
                        targetY = row - Height;
                        VelocityY = 0f;
                        break;
                    }
                }
            }

            Y = targetY;

            // —— ③ 水平积分：按移动方向扫侧边覆盖的**行**，撞上就停在格子内沿 ——
            // 竖直位移已经落定，因此侧边条带用的是更新后的 Y。
            float targetX = X + VelocityX;
            int rowMin = FloorToInt(Y + Epsilon);
            int rowMax = FloorToInt(Y + Height - Epsilon);

            if (VelocityX > 0f)
            {
                int colFirst = FloorToInt(X + Width * 0.5f - Epsilon) + 1;
                int colLast = FloorToInt(targetX + Width * 0.5f - Epsilon);
                for (int col = colFirst; col <= colLast; col++)
                {
                    if (AnySolidInColumn(world, col, rowMin, rowMax))
                    {
                        targetX = col - Width * 0.5f;
                        VelocityX = 0f;
                        break;
                    }
                }
            }
            else if (VelocityX < 0f)
            {
                int colFirst = FloorToInt(X - Width * 0.5f + Epsilon) - 1;
                int colLast = FloorToInt(targetX - Width * 0.5f + Epsilon);
                for (int col = colFirst; col >= colLast; col--)
                {
                    if (AnySolidInColumn(world, col, rowMin, rowMax))
                    {
                        targetX = col + 1 + Width * 0.5f;
                        VelocityX = 0f;
                        break;
                    }
                }
            }

            X = targetX;

            // —— ④ 世界边界夹取：撞到世界外壁等同于撞到静止实体，速度清零 ——
            float halfWidth = Width * 0.5f;
            float maxX = world.Width - halfWidth;
            if (maxX < halfWidth)
            {
                // 世界比动物还窄（极端小世界）：退化为中心对齐，避免 min > max。
                X = world.Width * 0.5f;
                VelocityX = 0f;
            }
            else if (X < halfWidth)
            {
                X = halfWidth;
                VelocityX = 0f;
            }
            else if (X > maxX)
            {
                X = maxX;
                VelocityX = 0f;
            }

            // 世界高过动物时上界是 world.Height − Height，矮于动物时退化为 0（贴地），保证 Y ≥ 0 恒成立。
            float maxY = world.Height - Height;
            if (maxY < 0f)
            {
                maxY = 0f;
            }

            if (Y < 0f)
            {
                Y = 0f;
                VelocityY = 0f;
            }
            else if (Y > maxY)
            {
                Y = maxY;
                VelocityY = 0f;
            }

            // —— ⑤ 收尾：刷新支撑与淹没状态（渲染 / 下一步决策读的就是这份结果）——
            // 底边正下方（Y − ε）覆盖列里任一固体即为有支撑；Y 被夹到 0 也算落地。
            IsGrounded = false;
            if (Y <= 0f)
            {
                IsGrounded = true;
            }
            else
            {
                int groundRow = FloorToInt(Y - Epsilon);
                int groundColMin = FloorToInt(X - Width * 0.5f + Epsilon);
                int groundColMax = FloorToInt(X + Width * 0.5f - Epsilon);
                for (int col = groundColMin; col <= groundColMax; col++)
                {
                    if (CreatureTerrain.IsSolid(world, col, groundRow))
                    {
                        IsGrounded = true;
                        break;
                    }
                }
            }

            IsSubmerged = CreatureTerrain.IsWater(world, FloorToInt(X), FloorToInt(Y + Height * 0.5f));

            // 支撑面把向下的速度吃掉：恰好落在格边界上时，竖直扫描可能一行都没扫到（没有「进入」任何行），
            // 但底边已经贴住固体——此时若留着负速度，会与「贴地」的物理语义矛盾。补一条与夹取落地同口径的收尾。
            if (IsGrounded && VelocityY < 0f)
            {
                VelocityY = 0f;
            }
        }

        /// <summary>行内任一覆盖列是固体。</summary>
        private static bool AnySolidInRow(ParticleWorld world, int row, int colMin, int colMax)
        {
            for (int col = colMin; col <= colMax; col++)
            {
                if (CreatureTerrain.IsSolid(world, col, row))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>列内任一覆盖行是固体。</summary>
        private static bool AnySolidInColumn(ParticleWorld world, int col, int rowMin, int rowMax)
        {
            for (int row = rowMin; row <= rowMax; row++)
            {
                if (CreatureTerrain.IsSolid(world, col, row))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>向下取整（不走 Mathf，避免任何 UnityEngine 依赖进入物理路径）。</summary>
        private static int FloorToInt(float value)
        {
            int truncated = (int)value;
            return value < truncated ? truncated - 1 : truncated;
        }
    }
}
