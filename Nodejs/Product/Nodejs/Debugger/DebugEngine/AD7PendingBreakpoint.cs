﻿//*********************************************************//
//    Copyright (c) Microsoft. All rights reserved.
//    
//    Apache 2.0 License
//    
//    You may obtain a copy of the License at
//    http://www.apache.org/licenses/LICENSE-2.0
//    
//    Unless required by applicable law or agreed to in writing, software 
//    distributed under the License is distributed on an "AS IS" BASIS, 
//    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or 
//    implied. See the License for the specific language governing 
//    permissions and limitations under the License.
//
//*********************************************************//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudioTools;

namespace Microsoft.NodejsTools.Debugger.DebugEngine {
    // This class represents a pending breakpoint which is an abstract representation of a breakpoint before it is bound.
    // When a user creates a new breakpoint, the pending breakpoint is created and is later bound. The bound breakpoints
    // become children of the pending breakpoint.
    class AD7PendingBreakpoint : IDebugPendingBreakpoint2 {
        // The breakpoint request that resulted in this pending breakpoint being created.
        private readonly BreakpointManager _bpManager;
        private readonly IDebugBreakpointRequest2 _bpRequest;
        private readonly List<AD7BreakpointErrorEvent> _breakpointErrors = new List<AD7BreakpointErrorEvent>();
        private readonly AD7Engine _engine;
        private BP_REQUEST_INFO _bpRequestInfo;
        private NodeBreakpoint _breakpoint;
        private string _documentName;
        private bool _enabled, _deleted;

        public AD7PendingBreakpoint(IDebugBreakpointRequest2 pBpRequest, AD7Engine engine, BreakpointManager bpManager) {
            _bpRequest = pBpRequest;
            var requestInfo = new BP_REQUEST_INFO[1];
            EngineUtils.CheckOk(_bpRequest.GetRequestInfo(enum_BPREQI_FIELDS.BPREQI_BPLOCATION | enum_BPREQI_FIELDS.BPREQI_CONDITION | enum_BPREQI_FIELDS.BPREQI_ALLFIELDS, requestInfo));
            _bpRequestInfo = requestInfo[0];

            _engine = engine;
            _bpManager = bpManager;

            _enabled = true;
            _deleted = false;
        }

        public BP_PASSCOUNT PassCount {
            get { return _bpRequestInfo.bpPassCount; }
        }

        public string DocumentName {
            get {
                if (_documentName == null) {
                    var docPosition = (IDebugDocumentPosition2)(Marshal.GetObjectForIUnknown(_bpRequestInfo.bpLocation.unionmember2));
                    EngineUtils.CheckOk(docPosition.GetFileName(out _documentName));
                }
                return _documentName;
            }
        }

        #region IDebugPendingBreakpoint2 Members

        // Binds this pending breakpoint to one or more code locations.
        int IDebugPendingBreakpoint2.Bind() {
            if (CanBind()) {
                // Get the location in the document that the breakpoint is in.
                var startPosition = new TEXT_POSITION[1];
                var endPosition = new TEXT_POSITION[1];
                string fileName;
                var docPosition = (IDebugDocumentPosition2)(Marshal.GetObjectForIUnknown(_bpRequestInfo.bpLocation.unionmember2));
                EngineUtils.CheckOk(docPosition.GetRange(startPosition, endPosition));
                EngineUtils.CheckOk(docPosition.GetFileName(out fileName));

                _breakpoint = _engine.Process.AddBreakpoint(
                    fileName,
                    (int)startPosition[0].dwLine,
                    (int)startPosition[0].dwColumn,
                    _enabled,
                    AD7BoundBreakpoint.GetBreakOnForPassCount(_bpRequestInfo.bpPassCount),
                    _bpRequestInfo.bpCondition.bstrCondition);

                _bpManager.AddPendingBreakpoint(_breakpoint, this);
                _breakpoint.BindAsync().WaitAsync(TimeSpan.FromSeconds(2)).Wait();

                return VSConstants.S_OK;
            }

            // The breakpoint could not be bound. This may occur for many reasons such as an invalid location, an invalid expression, etc...
            // The sample engine does not support this, but a real world engine will want to send an instance of IDebugBreakpointErrorEvent2 to the
            // UI and return a valid instance of IDebugErrorBreakpoint2 from IDebugPendingBreakpoint2::EnumErrorBreakpoints. The debugger will then
            // display information about why the breakpoint did not bind to the user.
            return VSConstants.S_FALSE;
        }

        // Determines whether this pending breakpoint can bind to a code location.
        int IDebugPendingBreakpoint2.CanBind(out IEnumDebugErrorBreakpoints2 ppErrorEnum) {
            ppErrorEnum = null;

            if (!CanBind()) {
                // Called to determine if a pending breakpoint can be bound. 
                // The breakpoint may not be bound for many reasons such as an invalid location, an invalid expression, etc...
                // The sample engine does not support this, but a real world engine will want to return a valid enumeration of IDebugErrorBreakpoint2.
                // The debugger will then display information about why the breakpoint did not bind to the user.
                return VSConstants.S_FALSE;
            }

            return VSConstants.S_OK;
        }

        // Deletes this pending breakpoint and all breakpoints bound from it.
        int IDebugPendingBreakpoint2.Delete() {
            ClearBreakpointBindingResults();
            _deleted = true;
            return VSConstants.S_OK;
        }

        // Toggles the enabled state of this pending breakpoint.
        int IDebugPendingBreakpoint2.Enable(int fEnable) {
            _enabled = fEnable != 0;

            if (_breakpoint != null) {
                lock (_breakpoint) {
                    foreach (NodeBreakpointBinding binding in _breakpoint.GetBindings()) {
                        var boundBreakpoint = (IDebugBoundBreakpoint2)_bpManager.GetBoundBreakpoint(binding);
                        boundBreakpoint.Enable(fEnable);
                    }
                }
            }

            return VSConstants.S_OK;
        }

        // Enumerates all breakpoints bound from this pending breakpoint
        int IDebugPendingBreakpoint2.EnumBoundBreakpoints(out IEnumDebugBoundBreakpoints2 ppEnum) {
            ppEnum = null;

            if (_breakpoint != null) {
                lock (_breakpoint) {
                    IDebugBoundBreakpoint2[] boundBreakpoints = _breakpoint.GetBindings()
                        .Select(binding => _bpManager.GetBoundBreakpoint(binding))
                        .Cast<IDebugBoundBreakpoint2>().ToArray();

                    ppEnum = new AD7BoundBreakpointsEnum(boundBreakpoints);
                }
            }

            return VSConstants.S_OK;
        }

        // Enumerates all error breakpoints that resulted from this pending breakpoint.
        int IDebugPendingBreakpoint2.EnumErrorBreakpoints(enum_BP_ERROR_TYPE bpErrorType, out IEnumDebugErrorBreakpoints2 ppEnum) {
            // Called when a pending breakpoint could not be bound. This may occur for many reasons such as an invalid location, an invalid expression, etc...
            // Return a valid enumeration of IDebugErrorBreakpoint2 from IDebugPendingBreakpoint2::EnumErrorBreakpoints, allowing the debugger to
            // display information about why the breakpoint did not bind to the user.
            lock (_breakpointErrors) {
                IDebugErrorBreakpoint2[] breakpointErrors = _breakpointErrors.Cast<IDebugErrorBreakpoint2>().ToArray();
                ppEnum = new AD7ErrorBreakpointsEnum(breakpointErrors);
            }

            return VSConstants.S_OK;
        }

        // Gets the breakpoint request that was used to create this pending breakpoint
        int IDebugPendingBreakpoint2.GetBreakpointRequest(out IDebugBreakpointRequest2 ppBpRequest) {
            ppBpRequest = _bpRequest;
            return VSConstants.S_OK;
        }

        // Gets the state of this pending breakpoint.
        int IDebugPendingBreakpoint2.GetState(PENDING_BP_STATE_INFO[] pState) {
            if (_deleted) {
                pState[0].state = (enum_PENDING_BP_STATE)enum_BP_STATE.BPS_DELETED;
            } else if (_enabled) {
                pState[0].state = (enum_PENDING_BP_STATE)enum_BP_STATE.BPS_ENABLED;
            } else {
                pState[0].state = (enum_PENDING_BP_STATE)enum_BP_STATE.BPS_DISABLED;
            }

            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.SetCondition(BP_CONDITION bpCondition) {
            if (bpCondition.styleCondition == enum_BP_COND_STYLE.BP_COND_WHEN_CHANGED) {
                return VSConstants.E_NOTIMPL;
            }

            _bpRequestInfo.bpCondition = bpCondition;
            return VSConstants.S_OK;
        }

        int IDebugPendingBreakpoint2.SetPassCount(BP_PASSCOUNT bpPassCount) {
            _bpRequestInfo.bpPassCount = bpPassCount;
            return VSConstants.S_OK;
        }

        // Toggles the virtualized state of this pending breakpoint. When a pending breakpoint is virtualized, 
        // the debug engine will attempt to bind it every time new code loads into the program.
        // The sample engine will does not support this.
        int IDebugPendingBreakpoint2.Virtualize(int fVirtualize) {
            return VSConstants.S_OK;
        }

        #endregion

        private bool CanBind() {
            // Reject binding breakpoints which are deleted, not code file line, and on condition changed
            if (_deleted ||
                _bpRequestInfo.bpLocation.bpLocationType != (uint)enum_BP_LOCATION_TYPE.BPLT_CODE_FILE_LINE ||
                _bpRequestInfo.bpCondition.styleCondition == enum_BP_COND_STYLE.BP_COND_WHEN_CHANGED) {
                return false;
            }

            return true;
        }

        public void AddBreakpointError(AD7BreakpointErrorEvent breakpointError) {
            _breakpointErrors.Add(breakpointError);
        }

        // Remove all of the bound breakpoints for this pending breakpoint
        public void ClearBreakpointBindingResults() {
            if (_breakpoint != null) {
                lock (_breakpoint) {
                    foreach (NodeBreakpointBinding binding in _breakpoint.GetBindings()) {
                        var boundBreakpoint = (IDebugBoundBreakpoint2)_bpManager.GetBoundBreakpoint(binding);
                        if (boundBreakpoint != null) {
                            boundBreakpoint.Delete();
                            binding.Remove().WaitAndUnwrapExceptions();
                        }
                    }
                }

                _bpManager.RemovePendingBreakpoint(_breakpoint);
                _breakpoint.Deleted = true;
                _breakpoint = null;
            }

            _breakpointErrors.Clear();
        }
    }
}