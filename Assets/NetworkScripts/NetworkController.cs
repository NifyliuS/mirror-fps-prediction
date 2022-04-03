using System.Collections;
using System.Collections.Generic;
using Mirror;
using UnityEngine;

namespace NetworkScripts {
  /*
 * We want to manage ping frequency based on the situation rather than static:
 * Idle - When all is well and the tick is in sync with the incoming information
 * Initial - When we first connect we want to quickly scan the network state
 * Verifying - When a change was detected we want to allow for more pings to verify that the change is real and not a spike
 * Accelerated - When there is a change in ping we want to give more pings
 */
  public enum TickPingState {
    Idle,
    Initial,
    Verifying,
    Accelerated,
  }

  /* Structs for Ping/Pong with the host/server */
  public struct ServerPingPayload {
    public int ClientTickNumber;
  }

  public struct ServerPongPayload {
    public int ClientTickNumber;
    public int ServerTickNumber;
  }

  /* Struct for saved ping item - used on the client to compare */
  public struct PingItem {
    public bool IsSet;             // We are using circular buffers so we want to know witch tick is set
    public int  ClientNetworkTick; // Client tick at the time of reception ( used to compare server and client network ticks )
    public int  ClientTickOffset;  // Offset ( 1/2 rtt converted to ticks )
    public int  ServerNetworkTick; // Tick as received from the host/server 
    public int  TickDiff;          // Difference between Client and Server Ticks 
  }

  public class NetworkController : NetworkBehaviour {
    /********/
    /* Controller Configurations */
    private static int TickHistorySize = 1024; // Circular buffer size

    private static int PingConsolidationAllowedDification = 1;  // Tick offset ( 1/2 rtt ) forgiveness - we dont want to adjust ticks when there is a small diviation
    private static int TickConsolidationSize              = 10; // Number of ticks to collect before running verification ( only check with new ticks ) - aka - get x ticks before averaging
    private static int TickConsolidationVerificationSize  = 25; // How many times the ping has to be different for the system to adjust ping ( in case network improved by 1 tick )
    private static int PingConsolidationSize              = 10; // Amount of history pings to consider when checking network Tick ( mainly used in idle mode ) - aka average last x ticks
    private static int PingAdjustmentSize                 = 30; // What is the threshhold of idle tick adjustment ( +- 1 )


    /* Instantiate instances - used for Static level access */
    private static NetworkController _instance;
    private static NetworkTick       _networkTick;
    private static bool              _isReady = false;

    public static NetworkController Instance => _instance;
    public static NetworkTick       Tick     => _networkTick;
    public static bool              IsReady  => _isReady;

    /* Private Variables */
    private TickPingState _tickPingState       = TickPingState.Initial;
    private PingItem[]    _tickHistory         = new PingItem[TickHistorySize]; // Circular buffer ping item history
    private int           _tickPingCount       = 0;                             // Number of pings ( total )
    private int           _skipPhysicsSteps    = 0;                             // How many physics steps to skip 
    private int           _forwardPhysicsSteps = 0;                             // How many physics steps to fast forward
    private int           _clientTickNumber    = 0;                             // Number of ticks on the client - used for measuring tick offsets


    private ServerPongPayload _lastReceivedPong;       // We only save 1 last ping ( in case we receive multiple pings per fixed Update )
    private bool              _isReceivedPong = false; // Flag to tell the code to do ping verification

    /* Instantiate NetworkTick static Instance locally + Add Network Controller as well */
    private void Awake() {
      if (_instance != null && _instance != this) {
        Destroy(this.gameObject);
      }
      else {
        _instance = this;
        _networkTick = new NetworkTick();
      }
    }


    /*************************/
    /* Client Only functions */
    /*************************/
    [Client] // Queue physics adjustment
    private void AdjustClientPhysicsTick(int tickAdjustment) {
      if (tickAdjustment > 0) {
        _forwardPhysicsSteps += tickAdjustment;
      }
      else {
        _skipPhysicsSteps += -tickAdjustment;
      }
    }

    [Client] // Update Client tick number based on Time.time
    private void UpdateClientTick() {
      _clientTickNumber = Mathf.RoundToInt((float) (Time.time * _networkTick.GetServerTickPerSecond)); // Convert time to Ticks
    }

    [Client] // Update server tick based on offset
    private void UpdateServerTick() {
      _networkTick.SetServerTick(_clientTickNumber - _networkTick.GetTickLocalOffset());
    }

    [Client]  // We are not always comparing ticks so we want to move server tick locally even if no pings are happening
    private void AdvanceServerTick() {
      _networkTick.SetServerTick(_networkTick.GetServerTick() + 1);
    }
    
    /*******************************/
    /* Client Server Communication */
    /*******************************/
    [Command(requiresAuthority = false, channel = Channels.Unreliable)]
    private void CmdPingServer(ServerPingPayload clientPing) {
      // Once we got ping from client we want to send the current server tick immediately 
      RpcServerPong(new ServerPongPayload() {
        ClientTickNumber = clientPing.ClientTickNumber,
        ServerTickNumber = _networkTick.GetServerTick(),
      });
    }

    [ClientRpc(channel = Channels.Unreliable)]
    private void RpcServerPong(ServerPongPayload serverPong) {
      /* We only want to get the most recent pong from the server and ignore duplicates or throttled pongs */
      if (_lastReceivedPong.ServerTickNumber < serverPong.ServerTickNumber) {
        _lastReceivedPong = serverPong;
        _isReceivedPong = true;
      }
    }

    [Client]
    private void TickPing() {
      /* We want to decide when to sent the ping - i use this funky method because its convenient */
      bool queuePing = false;
      switch (_tickPingState) {
        case TickPingState.Initial:
          queuePing = _clientTickNumber % 10 == 0;
          break;
        case TickPingState.Verifying:
          queuePing = _clientTickNumber % 10 == 0;
          break;
        case TickPingState.Accelerated:
          queuePing = _clientTickNumber % 20 == 0;
          break;
        case TickPingState.Idle:
          queuePing = _clientTickNumber % 50 == 0;
          break;
      }

      if (queuePing) {
        CmdPingServer(new ServerPingPayload() {
          ClientTickNumber = _clientTickNumber,
        });
      }
    }

    /********************************/
    /* Client Fixed Update Handling */
    /********************************/
    [Client]
    private void ClientFixedUpdate() {
      TickPing(); //Check if we want to send ping to server
      UpdateClientTick();
      UpdateServerTick();
      HandleServerPong();
      AdvanceServerTick();
      PhysicsStep();
    }

    /***************************/
    /* Client Physics Handling */
    /***************************/
    [Client]
    private void PhysicsStep() {
      float deltaTime = Time.deltaTime;
      if (_skipPhysicsSteps > 0) {
        _skipPhysicsSteps = PhysicStepSkip(_skipPhysicsSteps, deltaTime);
        return;
      }

      if (_forwardPhysicsSteps > 0) {
        _forwardPhysicsSteps = PhysicStepFastForward(_forwardPhysicsSteps, deltaTime);
        return;
      }

      PhysicStep(deltaTime);
    }

    [Client]
    public virtual void PhysicStep(float deltaTime) { }

    [Client]
    public virtual int PhysicStepSkip(int skipSteps, float deltaTime) {
      return skipSteps - 1; // In case someone wants to skip more than 1 step on each FixedUpdate
    }

    [Client]
    public virtual int PhysicStepFastForward(int fastForwardSteps, float deltaTime) {
      return 0; //In case someone wants to fast forward ticks not all at once
    }

    /********************************************************/
    /* Hepler functions used to calculate and adjust things */
    /********************************************************/

    private PingItem GetPing(int index) {
      return _tickHistory[index % TickHistorySize];
    }

    private int AddPing(PingItem pingItem) {
      _tickHistory[_tickPingCount % TickHistorySize] = pingItem;
      _tickPingCount++;
      return _tickPingCount;
    }
  }
}