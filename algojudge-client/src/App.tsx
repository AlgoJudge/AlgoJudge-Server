import '@mantine/core/styles.css';
import '@mantine/notifications/styles.css';
import './App.css';

import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { RouterProvider, createBrowserRouter } from 'react-router-dom';
import Layout from './Layout';
import Home from './pages/home/Home';
import Login from './pages/login/Login';
import Manage from './pages/manage/Manage';
import Register from './pages/register/Register';
import Authentication from './routers/Authentication';
import AppLayout from './layouts/app/AppLayout';
import ActivitiesPage from './pages/activities/ActivitiesPage';
import ProblemsPage from './pages/problems/ProblemsPage';
import SubmitPage from './pages/submit/SubmitPage';
import SubmissionsPage from './pages/submissions/SubmissionsPage';
import RankingPage from './pages/ranking/RankingPage';
import QuestionsPage from './pages/questions/QuestionsPage';
import RulesPage from './pages/rules/RulesPage';
import ProblemPage from './pages/problem/ProblemPage';

function App() {

    const router = createBrowserRouter([
        {
            path: "/",
            element: <Layout />,
            children: [
                {
                    path: "/",
                    element: <Home />
                },
                {
                    path: "/login",
                    element: <Login />
                },
                {
                    path: "/register",
                    element: <Register />
                },
                {
                    path: "/manage",
                    element: <Authentication><Manage /></Authentication>
                }
            ]
        },
        {
            path: "/",
            element: <AppLayout />,
            children: [
                {
                    path: "/activities",
                    element: <ActivitiesPage />
                },
                {
                    path: "/activity/:activityId/problems",
                    element: <ProblemsPage />
                },
                {
                    path: "/activity/:activityId/submit/:problemId?",
                    element: <SubmitPage />
                },
                {
                    path: "/activity/:activityId/submissions",
                    element: <SubmissionsPage />
                },
                {
                    path: "/activity/:activityId/ranking",
                    element: <RankingPage />
                },
                {
                    path: "/activity/:activityId/questions",
                    element: <QuestionsPage />
                },
                {
                    path: "/activity/:activityId/rules",
                    element: <RulesPage />
                },
                {
                    path: "/activity/:activityId/problem/:problemId",
                    element: <ProblemPage />
                }
            ]
        }
    ], { basename: import.meta.env.BASE_URL });

    return (
            <MantineProvider>
                <Notifications />
                <RouterProvider router={router} />
            </MantineProvider>
    );
}

export default App;