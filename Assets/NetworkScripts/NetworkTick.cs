using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace NetworkScripts{
  public class NetworkTick{
    /* Configuratuins */

    private static int _ticksPerSecond = Mathf.RoundToInt(1 / Time.fixedDeltaTime); // Get number ot ticks per second - useful for converting time to ticks

    /* Initial variables */
    private static bool _isReady = false;
    private static uint _serverBaseTick = 0;
    private static uint _clientPredictionTick = 0;

    public bool ready => _isReady;
    public uint baseTick => _serverBaseTick;
    public uint predictionTick => _clientPredictionTick;
    /***************************************/
    /* Static variables for project access */
    /***************************************/

    /* Tick int variables */
    public static bool IsReady => _isReady;
    
    public static int TickPerSecond => _ticksPerSecond;
    public static uint ServerBaseTick => _serverBaseTick;
    public static uint ClientPredictionTick => _clientPredictionTick;
    public static float ServerBaseTime => (float)_serverBaseTick / _ticksPerSecond;
    public static float ClientPredictionTime => (float)_clientPredictionTick / _ticksPerSecond;

    /* Network Tick Control functions  */

    public void SetIsReady(bool isReady) => _isReady = isReady;
    public void SetServerTickPerSecond(int newTick) => _ticksPerSecond = newTick;
    public void SetServerBaseTick(uint newTick) => _serverBaseTick = newTick;
    public void SetClientPredictionTick(uint newTick) => _clientPredictionTick = newTick;
  }
}