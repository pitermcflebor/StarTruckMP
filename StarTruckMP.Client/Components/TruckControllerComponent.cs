using UnityEngine;
using BepInEx;

namespace StarTruckMP.Client.Components;

public class TruckControllerComponent : MonoBehaviour
{
    private Rigidbody _rb;
    
    public Vector3 TargetPosition;
    public Quaternion TargetRotation;
    public Vector3 TargetVelocity;

    // Interpolation
    private float _lerpSpeed = 15f;
    
    private bool _hasFirstUpdate = false;
    
    // hide until we get the real position
    private GameObject _npcTruckVisual;

    void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        if (_rb != null)
        {
            _rb.isKinematic = true;
            _rb.interpolation = RigidbodyInterpolation.Interpolate;
        }
        
        _npcTruckVisual = transform.Find("NPCTruck")?.gameObject;
    }

    void FixedUpdate()
    {
        if (!_hasFirstUpdate || _rb == null) return;
        
        // move smoothly
        _rb.MovePosition(Vector3.Lerp(transform.position, TargetPosition, _lerpSpeed * Time.fixedDeltaTime));
        _rb.MoveRotation(Quaternion.Slerp(transform.rotation, TargetRotation, _lerpSpeed * Time.fixedDeltaTime));
    }
    
    public void ApplyNetworkState(Vector3 pos, Quaternion rot, Vector3 vel)
    {
        TargetPosition = pos;
        TargetRotation = rot;
        TargetVelocity = vel;

        if (!_hasFirstUpdate)
        {
            _hasFirstUpdate = true;
            
            // move directly without lerp
            if (_rb != null)
            {
                _rb.position = pos;
                _rb.rotation = rot;
            }
            else
            {
                transform.position = pos;
                transform.rotation = rot;
            }

            // show now, we have a position
            if (_npcTruckVisual != null)
                _npcTruckVisual.SetActive(true);
        }
    }
}