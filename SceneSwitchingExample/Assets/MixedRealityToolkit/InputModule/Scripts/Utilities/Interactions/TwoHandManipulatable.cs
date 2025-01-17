// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using MixedRealityToolkit.InputModule.EventData;
using MixedRealityToolkit.InputModule.InputHandlers;
using MixedRealityToolkit.InputModule.InputSources;
using MixedRealityToolkit.UX.BoundingBoxes;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Assertions;


namespace MixedRealityToolkit.InputModule.Utilities.Interations
{
    /// <summary>
    /// This script allows for an object to be movable, scalable, and rotatable with one or two hands. 
    /// You may also configure the script on only enable certain manipulations. The script works with 
    /// both HoloLens' gesture input and immersive headset's motion controller input.
    /// See Assets/MixedRealityToolkit-Examples/Input/Readme/README_TwoHandManipulationTest.md
    /// for instructions on how to use the script.
    /// </summary>
    public class TwoHandManipulatable : MonoBehaviour, IInputHandler, ISourceStateHandler
    {
        // Event that gets raised when user begins manipulating the object
        public event Action StartedManipulating;
        // Event that gets raised when the user ends manipulation
        public event Action StoppedManipulating;

        [SerializeField]
        [Tooltip("Transform that will be dragged. Defaults to the object of the component.")]
        private Transform HostTransform = null;

        [SerializeField]
        [Tooltip("To visualize the object bounding box, drop the MixedRealityToolkit/UX/Prefabs/BoundingBoxes/BoundingBoxBasic.prefab here. This is optional.")]
        private BoundingBox boundingBoxPrefab = null;

        public enum TwoHandedManipulation
        {
            Scale,
            Rotate,
            MoveScale,
            RotateScale,
            MoveRotateScale
        };

        [SerializeField]
        [Tooltip("What manipulation will two hands perform?")]
        private TwoHandedManipulation ManipulationMode = TwoHandedManipulation.Scale;

        [SerializeField]
        [Tooltip("Constrain rotation along an axis")]
        private TwoHandRotateLogic.RotationConstraint ConstraintOnRotation = TwoHandRotateLogic.RotationConstraint.None;

        [SerializeField]
        [Tooltip("If true, grabbing the object with one hand will initiate movement.")]
        private bool OneHandMovement = true;

        [Flags]
        private enum State
        {
            Start = 0x000,
            Moving = 0x001,
            Scaling = 0x010,
            Rotating = 0x100,
            MovingScaling = 0x011,
            RotatingScaling = 0x110,
            MovingRotatingScaling = 0x111
        };

        private BoundingBox boundingBoxInstance;
        private State currentState;
        private TwoHandMoveLogic m_moveLogic;
        private TwoHandScaleLogic m_scaleLogic;
        private TwoHandRotateLogic m_rotateLogic;
        // Maps input id -> position of hand
        private readonly Dictionary<uint, Vector3> m_handsPressedLocationsMap = new Dictionary<uint, Vector3>();
        // Maps input id -> input source. Then obtain position of input source using currentInputSource.TryGetGripPosition(currentInputSourceId, out inputPosition);
        private readonly Dictionary<uint, IInputSource> m_handsPressedInputSourceMap = new Dictionary<uint, IInputSource>();

        public BoundingBox BoundingBoxPrefab
        {
            set
            {
                boundingBoxPrefab = value;
            }

            get
            {
                return boundingBoxPrefab;
            }
        }

        public void SetManipulationMode(TwoHandedManipulation mode)
        {
            ManipulationMode = mode;
        }

        private void Awake()
        {
            m_moveLogic = new TwoHandMoveLogic();
            m_rotateLogic = new TwoHandRotateLogic(ConstraintOnRotation);
            m_scaleLogic = new TwoHandScaleLogic();
        }

        private void Start()
        {
            if (HostTransform == null)
            {
                HostTransform = transform;
            }
        }

        private void Update()
        {
            // Update positions of all hands
            foreach (var key in m_handsPressedInputSourceMap.Keys)
            {
                var inputSource = m_handsPressedInputSourceMap[key];
                Vector3 inputPosition = Vector3.zero;
                if (inputSource.TryGetGripPosition(key, out inputPosition))
                {
                    m_handsPressedLocationsMap[key] = inputPosition;
                }
            }

            if (currentState != State.Start)
            {
                UpdateStateMachine();
            }
        }

        private bool showBoundingBox
        {
            set
            {
                if (boundingBoxPrefab != null)
                {
                    if (boundingBoxInstance == null)
                    {
                        // Instantiate Bounding Box from the Prefab
                        boundingBoxInstance = Instantiate(boundingBoxPrefab) as BoundingBox;
                    }

                    if (value)
                    {
                        boundingBoxInstance.Target = this.gameObject;
                        boundingBoxInstance.gameObject.SetActive(true);
                    }
                    else
                    {
                        boundingBoxInstance.Target = null;
                        boundingBoxInstance.gameObject.SetActive(false);
                    }
                }
            }
        }

        private Vector3 GetInputPosition(InputEventData eventData)
        {
            Vector3 result = Vector3.zero;
            eventData.InputSource.TryGetGripPosition(eventData.SourceId, out result);
            return result;
        }

        public void OnInputDown(InputEventData eventData)
        {
            // Add to hand map
            m_handsPressedLocationsMap[eventData.SourceId] = GetInputPosition(eventData);
            m_handsPressedInputSourceMap[eventData.SourceId] = eventData.InputSource;
            UpdateStateMachine();
            eventData.Use();
        }

        public void OnInputUp(InputEventData eventData)
        {
            RemoveSourceIdFromHandMap(eventData.SourceId);
            UpdateStateMachine();
            eventData.Use();
        }

        public void OnSourceDetected(SourceStateEventData eventData)
        {
        }

        private void RemoveSourceIdFromHandMap(uint sourceId)
        {
            if (m_handsPressedLocationsMap.ContainsKey(sourceId))
            {
                m_handsPressedLocationsMap.Remove(sourceId);
            }

            if (m_handsPressedInputSourceMap.ContainsKey(sourceId))
            {
                m_handsPressedInputSourceMap.Remove(sourceId);
            }
        }

        public void OnSourceLost(SourceStateEventData eventData)
        {
            RemoveSourceIdFromHandMap(eventData.SourceId);
            UpdateStateMachine();
            eventData.Use();
        }

        private void UpdateStateMachine()
        {
            var handsPressedCount = m_handsPressedLocationsMap.Count;
            State newState = currentState;
            switch (currentState)
            {
                case State.Start:
                case State.Moving:
                    if (handsPressedCount == 0)
                    {
                        newState = State.Start;
                    }
                    else
                        if (handsPressedCount == 1 && OneHandMovement)
                    {
                        newState = State.Moving;
                    }
                    else if (handsPressedCount > 1)
                    {
                        switch (ManipulationMode)
                        {
                            case TwoHandedManipulation.Scale:
                                newState = State.Scaling;
                                break;
                            case TwoHandedManipulation.Rotate:
                                newState = State.Rotating;
                                break;
                            case TwoHandedManipulation.MoveScale:
                                newState = State.MovingScaling;
                                break;
                            case TwoHandedManipulation.RotateScale:
                                newState = State.RotatingScaling;
                                break;
                            case TwoHandedManipulation.MoveRotateScale:
                                newState = State.MovingRotatingScaling;
                                break;
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    break;
                case State.Scaling:
                case State.Rotating:
                case State.MovingScaling:
                case State.RotatingScaling:
                case State.MovingRotatingScaling:
                    // TODO: if < 2, make this go to start state ('drop it')
                    if (handsPressedCount == 0)
                    {
                        newState = State.Start;
                    }
                    else if (handsPressedCount == 1)
                    {
                        newState = State.Moving;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            InvokeStateUpdateFunctions(currentState, newState);
            currentState = newState;
        }
        private void InvokeStateUpdateFunctions(State oldState, State newState)
        {
            if (newState != oldState)
            {
                switch (newState)
                {
                    case State.Moving:
                        OnOneHandMoveStarted();
                        break;
                    case State.Start:
                        OnManipulationEnded();
                        break;
                    case State.RotatingScaling:
                    case State.MovingRotatingScaling:
                    case State.Scaling:
                    case State.Rotating:
                    case State.MovingScaling:
                        OnTwoHandManipulationStarted(newState);
                        break;
                }
                switch (oldState)
                {
                    case State.Start:
                        OnManipulationStarted();
                        break;
                    case State.Scaling:
                    case State.Rotating:
                    case State.RotatingScaling:
                    case State.MovingRotatingScaling:
                    case State.MovingScaling:
                        OnTwoHandManipulationEnded();
                        break;
                }
            }
            else
            {
                switch (newState)
                {
                    case State.Moving:
                        OnOneHandMoveUpdated();
                        break;
                    case State.Scaling:
                    case State.Rotating:
                    case State.RotatingScaling:
                    case State.MovingRotatingScaling:
                    case State.MovingScaling:
                        OnTwoHandManipulationUpdated();
                        break;
                    default:
                        break;
                }
            }
        }

        private void OnTwoHandManipulationUpdated()
        {
            var targetRotation = HostTransform.rotation;
            var targetPosition = HostTransform.position;
            var targetScale = HostTransform.localScale;

            if ((currentState & State.Moving) > 0)
            {
                targetPosition = m_moveLogic.Update(GetHandsCentroid(), targetPosition);
            }
            if ((currentState & State.Rotating) > 0)
            {
                targetRotation = m_rotateLogic.Update(m_handsPressedLocationsMap, HostTransform, targetRotation);
            }
            if ((currentState & State.Scaling) > 0)
            {
                targetScale = m_scaleLogic.Update(m_handsPressedLocationsMap);
            }

            HostTransform.position = targetPosition;
            HostTransform.rotation = targetRotation;
            HostTransform.localScale = targetScale;
        }

        private void OnOneHandMoveUpdated()
        {
            var targetPosition = m_moveLogic.Update(m_handsPressedLocationsMap.Values.First(), HostTransform.position);

            HostTransform.position = targetPosition;
        }

        private void OnTwoHandManipulationEnded()
        {
        }

        private Vector3 GetHandsCentroid()
        {
            Vector3 result = m_handsPressedLocationsMap.Values.Aggregate(Vector3.zero, (current, state) => current + state);
            return result / m_handsPressedLocationsMap.Count;
        }

        private void OnTwoHandManipulationStarted(State newState)
        {
            if ((newState & State.Rotating) > 0)
            {
                m_rotateLogic.Setup(m_handsPressedLocationsMap, HostTransform);
            }
            if ((newState & State.Moving) > 0)
            {
                m_moveLogic.Setup(GetHandsCentroid(), HostTransform);
            }
            if ((newState & State.Scaling) > 0)
            {
                m_scaleLogic.Setup(m_handsPressedLocationsMap, HostTransform);
            }
        }

        private void OnOneHandMoveStarted()
        {
            Assert.IsTrue(m_handsPressedLocationsMap.Count == 1);

            m_moveLogic.Setup(m_handsPressedLocationsMap.Values.First(), HostTransform);
        }
        private void OnManipulationStarted()
        {
            if (StartedManipulating != null)
            {
                StartedManipulating();
            }
            InputManager.Instance.PushModalInputHandler(gameObject);

            //Show Bounding Box visual on manipulation interaction
            showBoundingBox = true;
        }
        private void OnManipulationEnded()
        {
            if (StoppedManipulating != null)
            {
                StoppedManipulating();
            }
            InputManager.Instance.PopModalInputHandler();

            //Hide Bounding Box visual on release
            showBoundingBox = false;
        }
    }
}
