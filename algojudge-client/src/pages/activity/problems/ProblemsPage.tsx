import { Button, Card, Group, Overlay, Stack, Text, Title } from "@mantine/core";
import classes from "./ProblemsPage.module.css";
import { useEffect, useState } from "react";
import { useInterval } from "@mantine/hooks";
import { useTranslation } from "react-i18next";
import { Link, useNavigate } from "react-router-dom";

interface Problem {
    id: string,
    name: string
}

interface Round {
    id: string,
    name: string,
    isActive: boolean,
    startDate: string,
    endDate: string,
    problems?: Problem[]
}

const data: Round[] = [
    {
        id: "R1",
        name: "Round 1",
        isActive: true,
        startDate: "2025-03-18T10:00:00.000Z",
        endDate: "2025-03-18T11:00:00.000Z",
        problems: [
            {
                id: "P1",
                name: "Problem 1"
            },
            {
                id: "P2",
                name: "Problem 2"
            },
            {
                id: "P3",
                name: "Problem 3"
            },
            {
                id: "P4",
                name: "Problem 4"
            }
        ]
    },
    {
        id: "R2",
        name: "Round 2",
        isActive: false,
        startDate: "2025-03-22T10:00:00.000Z",
        endDate: "2025-03-22T11:00:00.000Z",
        problems: undefined
    }
];

const activityId = "PCN3";

const placeholderProblems: Problem[] = [
    {
        id: "P1",
        name: "Problem 1"
    },
    {
        id: "P2",
        name: "Problem 2"
    },
    {
        id: "P3",
        name: "Problem 3"
    },
    {
        id: "P4",
        name: "Problem 4"
    }
];

const Problem = (props: { problem: Problem, activityId: string }) => {
    const { t } = useTranslation();
    return (
        <Card className={classes.problem} component={Link} to={`/activity/${props.activityId}/problem/${props.problem.id}`}>
            <Group justify="space-between">
                <Text size="md">[{props.problem.id}] {props.problem.name}</Text>
                <Button component={Link} to={`/activity/${props.activityId}/submit/${props.problem.id}`}>{t("Submit")}</Button>
            </Group>
        </Card>
    );
}

const RoundOverlay = (props: { startDate: string }) => {
    const date1 = new Date(props.startDate);
    const date2 = new Date();
    const span = Math.floor((date1.getTime() - date2.getTime()) / 1000);
    const [time, setTime] = useState<number>(span);
    const interval = useInterval(() => {
        setTime(time <= 0 ? 0 : time - 1);
    }, 1000);
    useEffect(() => {
        interval.start();
        return interval.stop;
    }, []);
    return (
        <Overlay color="#fff" backgroundOpacity={0.3} blur={4}>
            <Stack className={classes.roundoverlay}>
                <Text size="xl" fw="bolder">Time to start:</Text>
                <Text size="xl" fw="bolder">{time} sec</Text>
            </Stack>
        </Overlay>
    );
}

const Round = (props: { round: Round, activityId: string }) => {
    const problems = (props.round.problems ?? placeholderProblems).map(p => <Problem key={p.id} problem={p} activityId={props.activityId} />);
    return (
        <Card className={classes.round}>
            <Group justify="space-between">
                <Title order={2}>{props.round.name}</Title>
                <Text>{(new Date(props.round.startDate).toLocaleString())} - {(new Date(props.round.endDate).toLocaleString())}</Text>
            </Group>
            {problems}
            {!props.round.isActive && <RoundOverlay startDate={props.round.startDate} />}
        </Card>
    );
}

export default function ProblemsPage() {
    const { t } = useTranslation();
    const rounds = data.map(r => <Round key={r.id} round={r} activityId={activityId} />);
    return (
        <>
            <Title>{t("Problems")}</Title>
            {rounds}
        </>
    )
}
