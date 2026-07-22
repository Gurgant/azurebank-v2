import { configureStore } from '@reduxjs/toolkit';
import { authReducer } from '../features/auth/authSlice';
import { sessionMiddleware } from '../features/auth/sessionMiddleware';
import { apiSlice } from '../features/api/apiSlice';

export const store = configureStore({
  reducer: {
    auth: authReducer,
    [apiSlice.reducerPath]: apiSlice.reducer,
  },
  middleware: (getDefaultMiddleware) =>
    getDefaultMiddleware().concat(apiSlice.middleware, sessionMiddleware),
  // Explicit: never expose the state tree (which transiently holds a withdraw mutation's
  // args, incl. the PIN) to the Redux DevTools extension in production.
  devTools: import.meta.env.DEV,
});

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
