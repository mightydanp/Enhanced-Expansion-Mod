using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace Sprint{
    public class SprintMain : Mod{
        //Properties
        private SprintConfig Config;

        //<summary>The stamina cost per tick for sprinting.</summary>
        private float StamCost;

        //<summary>Number of seconds of sprinting before player is winded, or 0 to disable windedness.</summary>
        private int WindedStep;

        //<summary>Whether the player can get winded.</summary>
        private bool EnableWindedness;

        //<summary>Whether we're operating the button as a toggle.</summary>
        private bool EnableToggle;

        //<summary>How long player has been sprinting.</summary>
        private int SprintTime;

        //<summary>How many "stages" of winding (+1 per windedStep seconds) the player has accumulated.</summary>
        private int StepsProgressed;

        //<summary>Multiplier to <see cref="StamCost"/> based on windedness.</summary>
        private int WindedAccumulated;

        //<summary>When winded but no longer sprinting, this governs how quickly windedness goes away.</summary>
        private int WindedCooldownStep;

        //<summary>Whether the sprint function is toggled on.</summary>
        private bool SprintToggledOn;

        //Not really sure I understand why it was necessary to use these identifier ints rather than just comparing references to Buff objects, but these probably should be deprecated.
        private const int SprintBuffID = 58012395;
        private const int CooldownBuffID = 6890125;

        private Buff SprintBuff;
        private Buff CooldownBuff;

        //<summary>Does nothing, exists to time cooldown of windedness.</summary>
        private Buff WindedBuff;

        private KeyboardState CurrentKeyboardState;

        private Keys[] RunKey;

        private bool NeedCooldown;

        //<summary>The sprint time.</summary>
        private readonly int SprintBuffDuration = 1000;

        //<summary>When to check to refresh buffs.</summary>
        private readonly int TimeoutCheck = 35;

        //<summary>The current milliseconds left for a buff.</summary>
        private int CurrentTimeLeft;

        //<summary>How long to refresh a status if relevant.</summary>
        private int RefreshTime;

        //<summary>How little stamina player must have for sprint to refresh.</summary>
        private float MinStaminaToRefresh = 30f;


        // Public methods
        //<summary>The mod entry point, called after the mod is first loaded.</summary>
        //<param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper){
            this.SprintBuff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 1, "Sprinting", "Sprinting");
            this.WindedBuff = new Buff(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, "Winded", "Winded");

            // read config
            this.Config = helper.ReadConfig<SprintConfig>();
            this.StamCost = Math.Max(1, this.Config.StamCost);
            this.WindedStep = this.Config.WindedStep;
            if (this.WindedStep > 0){
                this.EnableWindedness = true;
                this.WindedCooldownStep = this.WindedStep * 200;  //Recovering from winded-ness take 1/5 the time spent being winded.
                this.WindedStep *= 1000; // convert config-based times to ms
            }
            this.EnableToggle = this.Config.ToggleMode;
            this.RunKey = null;

            //hook events
            GameEvents.UpdateTick += this.GameEvents_UpdateTick;
            ControlEvents.KeyPressed += this.ControlEvents_KeyPressed;

            //log info
            this.Monitor.Log($"Stamina cost: {this.StamCost}, winded step: {this.WindedStep}, toggle mode: {this.EnableToggle}", LogLevel.Trace);
        }


        //Private methods
        private void WindedTest(){
            this.Monitor.Log($"(Winded Status) sprint time: {this.SprintTime}, steps progressed: {this.StepsProgressed}, winded accumulated: {this.WindedAccumulated}");
        }

        //<summary>Detect key press.</summary>
        //<param name="sender">The event sender.</param>
        //<param name="e">The event arguments.</param>
        void ControlEvents_KeyPressed(object sender, EventArgsKeyPressed e){
            //do nothing if the conditions aren't favorable
            if (!Game1.shouldTimePass() || Game1.player.isRidingHorse())
                return;

            Keys pressed = e.KeyPressed;

           if (pressed == this.Config.SprintKey && this.EnableToggle){
                this.SprintToggledOn = !this.SprintToggledOn;

                //Re-enable autorun if sprinting because sprint-walking is, uh, dumb.
                if (!Game1.options.autoRun && this.SprintToggledOn)
                    Game1.options.autoRun = true;
            }
            //At this point, the run button is basically a toggle for autorun. Not sure if this is the best feature honestly but eh.
            else if (this.RunKey.Contains(pressed) && this.EnableToggle){
                Game1.options.autoRun = !Game1.options.autoRun;

                //Disable sprinting if we're no longer running because sprint-walking is, uh, dumb.
                if (this.SprintToggledOn && !Game1.options.autoRun)
                    this.SprintToggledOn = false;
            }
        }

        //Do this every tick. Checks for persistent effects, does first-run stuff.
        private void GameEvents_UpdateTick(object sender, EventArgs e){
            //This is complicated and necessary because SDV stores the run button as an array of buttons. Theoretically we may have more than one.
            if (this.RunKey == null){
                this.RunKey = new Keys[Game1.options.runButton.Length];

                int i = 0;

                foreach (InputButton button in Game1.options.runButton){
                    this.RunKey[i] = button.key;
                    i++;
                }
            }

            //Cancel toggled sprinting if on horseback
            if (Game1.player.isRidingHorse() && this.EnableToggle && this.SprintToggledOn)
                this.SprintToggledOn = false;

            //If time cannot pass we should return (desireable??)
            if (!Game1.shouldTimePass())
                return;

            //Apply sprint buff if needed.
            this.CurrentKeyboardState = Keyboard.GetState();
            if ((this.CurrentKeyboardState.IsKeyDown(this.Config.SprintKey) || this.SprintToggledOn) && !Game1.player.isRidingHorse()){
                if (this.SprintTime < 0)
                    this.SprintTime = 0;

                foreach (Buff buff in Game1.buffsDisplay.otherBuffs){
                    if (buff == SprintBuff){
                        this.CurrentTimeLeft = buff.millisecondsDuration;

                        if (this.CurrentTimeLeft <= this.TimeoutCheck){
                            this.RefreshTime = this.SprintBuffDuration - this.CurrentTimeLeft;
                            this.SprintTime += this.RefreshTime;

                            if (this.EnableWindedness) {
                                if (this.SprintTime > this.WindedStep){
                                    this.StepsProgressed = (int)Math.Floor((double)(this.SprintTime / this.WindedStep));
                                    this.WindedAccumulated = (int)(this.StamCost * this.StepsProgressed);
                                }

                                this.WindedTest();
                            }else
                                this.Monitor.Log("Refreshing sprint...");

                            //Only refresh duration if more than min stam remains.
                            if (Game1.player.stamina > this.MinStaminaToRefresh)
                                buff.millisecondsDuration += this.RefreshTime;

                            //These are checks so that, if somehow we end up with a total stamina cost greater than current stamina, we won't get a negative result. (Not sure if needed?)
                            if (Game1.player.stamina > (this.StamCost + this.WindedAccumulated))
                                Game1.player.stamina -= (this.StamCost + this.WindedAccumulated);
                            else
                                Game1.player.stamina = 0;
                        }
                        return;
                    }
                }

                //Only grant the buff if player has more than min stam
                if (Game1.player.stamina > this.MinStaminaToRefresh) {
                    this.Monitor.Log("Starting to sprint...");

                    SprintBuff.millisecondsDuration = this.SprintBuffDuration;
                    SprintBuff.which = SprintMain.SprintBuffID;
                    Game1.buffsDisplay.addOtherBuff(SprintBuff);
                    Game1.player.Stamina -= (this.StamCost + this.WindedAccumulated);
                }
            }else if (this.EnableWindedness && this.SprintTime > 0){
                foreach (Buff buff in Game1.buffsDisplay.otherBuffs){
                    if (buff == WindedBuff){
                        this.CurrentTimeLeft = buff.millisecondsDuration;

                        if (this.CurrentTimeLeft <= this.TimeoutCheck){
                            this.RefreshTime = this.WindedCooldownStep - this.CurrentTimeLeft;

                            if (this.WindedAccumulated > 0){
                                this.StepsProgressed -= 1;
                                this.WindedAccumulated -= (int)this.StamCost;
                                buff.millisecondsDuration += this.RefreshTime;
                                this.SprintTime -= this.WindedStep;
                            }else
                                this.SprintTime -= this.RefreshTime;

                            if (this.WindedAccumulated < 0)
                                this.WindedAccumulated = 0; // just in case

                            this.WindedTest();
                        }
                        return;
                    }
                }

                WindedBuff.millisecondsDuration = this.WindedCooldownStep;
                this.WindedBuff.glow = this.SprintTime > this.WindedStep
                    ? Color.Khaki
                    : Color.Transparent;
                Game1.buffsDisplay.addOtherBuff(WindedBuff);

                this.WindedTest();
            }
        }
    }
}
